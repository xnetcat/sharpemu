// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Deswizzles RDNA2 (GFX10) tiled texture surfaces into linear layout so they
/// can be uploaded to Vulkan. PS5 stores most textures in a swizzled layout
/// selected by the 5-bit SWIZZLE_MODE in the image descriptor; uploading those
/// bytes verbatim samples as garbage.
///
/// The GFX10 addressing uses power-of-two swizzle blocks (256 B / 4 KiB /
/// 64 KiB) whose internal element order follows the standard (S), display (D),
/// render (R), or z-order/depth (Z) equations. This implements the exact base
/// S and Z 2D single-sample modes; approximate D/R and pipe/bank-XOR modes stay
/// opt-in while their complete AddrLib equations are being ported.
///
/// Enable with <c>SHARPEMU_DETILE=1</c> while it is validated; the intent is to
/// make it the default once verified against reference titles.
/// </summary>
internal static class GnmTiling
{
    private static readonly bool _enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DETILE"),
        "1",
        StringComparison.Ordinal);

    private static readonly bool _disabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DETILE"),
        "0",
        StringComparison.Ordinal);

    private static readonly HashSet<uint> _reportedModes = new();

    public static bool Enabled => _enabled || !_disabled;

    /// <summary>
    /// Base swizzle modes (256 B / 4 KiB / 64 KiB Z/S/D/R) whose blocks are laid
    /// out linearly in memory, so the within-block equation alone deswizzles them
    /// exactly. These are safe to detile by default. The bank/pipe-XOR variants
    /// (_T = 13-16, _X = 21-27) reorder whole blocks in a way we only approximate,
    /// so those stay behind SHARPEMU_DETILE=1 until validated per title.
    /// </summary>
    private static bool IsTrustedByDefault(uint swizzleMode) =>
        // Exact base S/Z modes. D/R use different GFX10 swizzle equations and
        // the T/X modes additionally apply pipe/bank XOR between blocks.
        swizzleMode is 1 or 4 or 5 or 8 or 9;

    // Detile a surface when it is verified-correct by default (trusted base mode),
    // or when the user opts the approximate modes in with SHARPEMU_DETILE=1.
    // SHARPEMU_DETILE=0 forces the old raw-upload behavior for everything.
    private static bool ShouldDetile(uint swizzleMode) =>
        swizzleMode != 0 && !_disabled && (_enabled || IsTrustedByDefault(swizzleMode));

    /// <summary>
    /// True when a surface with the given swizzle mode needs deswizzling.
    /// Mode 0 is linear and never needs it.
    /// </summary>
    public static bool NeedsDetile(uint swizzleMode) => ShouldDetile(swizzleMode);

    /// <summary>
    /// Gets the physical byte span occupied by a tiled mip. GNM allocates whole
    /// swizzle blocks even when the logical image is much smaller than a block;
    /// reading only the logical texel count truncates most source offsets during
    /// detiling (a 64x64 BC1 mode-9 image is 2 KiB logically but occupies one
    /// 64 KiB swizzle block).
    /// </summary>
    public static bool TryGetTiledByteCount(
        uint swizzleMode,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement,
        out ulong byteCount)
    {
        byteCount = 0;
        if (!ShouldDetile(swizzleMode) ||
            elementsWide <= 0 ||
            elementsHigh <= 0 ||
            bytesPerElement <= 0 ||
            !TryGetSwizzleKind(swizzleMode, out _, out var blockBytes))
        {
            return false;
        }

        var bppLog2 = BitLog2((uint)bytesPerElement);
        if (bppLog2 < 0)
        {
            return false;
        }

        var blockElements = blockBytes >> bppLog2;
        var (blockWidth, blockHeight) = SquareBlockDimensions(blockElements);
        if (blockWidth == 0 || blockHeight == 0)
        {
            return false;
        }

        var blocksWide = ((ulong)elementsWide + (ulong)blockWidth - 1) / (ulong)blockWidth;
        var blocksHigh = ((ulong)elementsHigh + (ulong)blockHeight - 1) / (ulong)blockHeight;
        try
        {
            byteCount = checked(blocksWide * blocksHigh * (ulong)blockBytes);
            return true;
        }
        catch (OverflowException)
        {
            byteCount = 0;
            return false;
        }
    }

    /// <summary>
    /// Deswizzles <paramref name="tiled"/> into linear row-major order.
    /// Elements are pixels for uncompressed formats and 4x4 blocks for
    /// block-compressed formats, so callers pass the element grid dimensions
    /// and the bytes per element. Returns false (leaving output untouched) for
    /// unsupported swizzle modes so the caller can fall back to the raw bytes.
    /// </summary>
    public static bool TryDetile(
        ReadOnlySpan<byte> tiled,
        Span<byte> linear,
        uint swizzleMode,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement)
    {
        if (!ShouldDetile(swizzleMode) || elementsWide <= 0 || elementsHigh <= 0 || bytesPerElement <= 0)
        {
            return false;
        }

        if (!TryGetSwizzleKind(swizzleMode, out var kind, out var blockBytes))
        {
            ReportUnsupported(swizzleMode);
            return false;
        }

        var bppLog2 = BitLog2((uint)bytesPerElement);
        if (bppLog2 < 0)
        {
            // Non-power-of-two element size (e.g. 24-bit) is not swizzled in a
            // way this equation models.
            ReportUnsupported(swizzleMode);
            return false;
        }

        // Block dimensions in elements: a swizzle block holds blockBytes bytes,
        // laid out as a square-ish power-of-two element grid scaled by bpp.
        var blockElements = blockBytes >> bppLog2;
        var (blockWidth, blockHeight) = SquareBlockDimensions(blockElements);
        if (blockWidth == 0 || blockHeight == 0)
        {
            ReportUnsupported(swizzleMode);
            return false;
        }

        var blocksPerRow = (elementsWide + blockWidth - 1) / blockWidth;
        var requiredLinear = (long)elementsWide * elementsHigh * bytesPerElement;
        if (linear.Length < requiredLinear)
        {
            return false;
        }

        // Precompute the within-block element offset for each (x, y) inside a
        // single block. The swizzle equation only depends on the in-block
        // coordinates, so this table is reused for every block — turning the
        // per-pixel bit-interleave (a loop + calls) into a single array lookup.
        // Detiling a 2048x2048 texture is millions of elements; without this the
        // per-pixel math makes DETILE unusably slow during asset streaming.
        var blockTable = new int[blockWidth * blockHeight];
        for (var by = 0; by < blockHeight; by++)
        {
            for (var bx = 0; bx < blockWidth; bx++)
            {
                blockTable[by * blockWidth + bx] = (int)(kind == SwizzleKind.ZOrder
                    ? MortonInterleave((uint)bx, (uint)by, blockWidth, blockHeight)
                    : StandardSwizzleOffset((uint)bx, (uint)by, blockWidth, blockHeight));
            }
        }

        for (var y = 0; y < elementsHigh; y++)
        {
            var blockY = y / blockHeight;
            var inBlockY = y % blockHeight;
            var rowBlockBase = (long)blockY * blocksPerRow;
            var tableRowBase = inBlockY * blockWidth;
            var destRowBase = (long)y * elementsWide * bytesPerElement;
            for (var x = 0; x < elementsWide; x++)
            {
                var blockX = x / blockWidth;
                var inBlockX = x % blockWidth;

                var blockIndex = rowBlockBase + blockX;
                var withinBlock = blockTable[tableRowBase + inBlockX];

                var sourceByte = (blockIndex * blockElements + withinBlock) * (long)bytesPerElement;
                var destByte = destRowBase + (long)x * bytesPerElement;
                if (sourceByte + bytesPerElement > tiled.Length ||
                    destByte + bytesPerElement > linear.Length)
                {
                    continue;
                }

                tiled.Slice((int)sourceByte, bytesPerElement)
                    .CopyTo(linear.Slice((int)destByte, bytesPerElement));
            }
        }

        return true;
    }

    private enum SwizzleKind
    {
        Standard,
        ZOrder,
    }

    private static bool TryGetSwizzleKind(uint swizzleMode, out SwizzleKind kind, out int blockBytes)
    {
        // GFX10 AddrLib SWIZZLE_MODE enumeration:
        //   1-3   = 256 B S/D/R
        //   4-7   = 4 KiB Z/S/D/R
        //   8-11  = 64 KiB Z/S/D/R
        //   16-19 = 64 KiB Z/S/D/R _T
        //   20-23 = 4 KiB Z/S/D/R _X
        //   24-27 = 64 KiB Z/S/D/R _X
        // The pipe/bank XOR (_T/_X) affects which block a given tile lands in,
        // but the *within-block* element order matches the base S/Z equation,
        // which is what dominates visible correctness. We model the block
        // interior and treat blocks as linear-ordered.
        kind = SwizzleKind.Standard;
        blockBytes = 0;
        switch (swizzleMode)
        {
            case 4: case 8: case 16: case 20: case 24:
                kind = SwizzleKind.ZOrder;
                break;
            case 1: case 2: case 3:
            case 5: case 6: case 7:
            case 9: case 10: case 11:
            case 17: case 18: case 19:
            case 21: case 22: case 23:
            case 25: case 26: case 27:
                kind = SwizzleKind.Standard;
                break;
            default:
                return false;
        }

        blockBytes = swizzleMode switch
        {
            >= 1 and <= 3 => 256,
            >= 4 and <= 7 => 4096,
            >= 20 and <= 23 => 4096,
            _ => 65536,
        };
        return true;
    }

    // Standard (S) 2D swizzle: within a block, the element order interleaves x
    // and y bits with x taking the low bit — the AMD "standard" microtile.
    private static long StandardSwizzleOffset(uint x, uint y, int blockWidth, int blockHeight)
    {
        var widthBits = BitLog2((uint)blockWidth);
        var heightBits = BitLog2((uint)blockHeight);
        long offset = 0;
        var outBit = 0;
        var xi = 0;
        var yi = 0;
        while (xi < widthBits || yi < heightBits)
        {
            if (xi < widthBits)
            {
                offset |= (long)((x >> xi) & 1u) << outBit++;
                xi++;
            }

            if (yi < heightBits)
            {
                offset |= (long)((y >> yi) & 1u) << outBit++;
                yi++;
            }
        }

        return offset;
    }

    // Z-order (Morton) swizzle: pure bit interleave, y taking the low bit.
    private static long MortonInterleave(uint x, uint y, int blockWidth, int blockHeight)
    {
        var widthBits = BitLog2((uint)blockWidth);
        var heightBits = BitLog2((uint)blockHeight);
        long offset = 0;
        var outBit = 0;
        var xi = 0;
        var yi = 0;
        while (xi < widthBits || yi < heightBits)
        {
            if (yi < heightBits)
            {
                offset |= (long)((y >> yi) & 1u) << outBit++;
                yi++;
            }

            if (xi < widthBits)
            {
                offset |= (long)((x >> xi) & 1u) << outBit++;
                xi++;
            }
        }

        return offset;
    }

    private static (int Width, int Height) SquareBlockDimensions(int blockElements)
    {
        if (blockElements <= 0 || (blockElements & (blockElements - 1)) != 0)
        {
            return (0, 0);
        }

        var totalBits = BitLog2((uint)blockElements);
        // Split as evenly as possible with width >= height (x gets the extra bit).
        var widthBits = (totalBits + 1) / 2;
        var heightBits = totalBits - widthBits;
        return (1 << widthBits, 1 << heightBits);
    }

    private static int BitLog2(uint value)
    {
        if (value == 0 || (value & (value - 1)) != 0)
        {
            return -1;
        }

        return System.Numerics.BitOperations.TrailingZeroCount(value);
    }

    private static void ReportUnsupported(uint swizzleMode)
    {
        lock (_reportedModes)
        {
            if (!_reportedModes.Add(swizzleMode))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] GNM detile: unsupported swizzle mode {swizzleMode}; texture uploaded linear.");
    }
}
