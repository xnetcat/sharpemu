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
/// 64 KiB) whose internal element order follows the "standard" (S), rotated
/// (R) or z-order/depth (Z) swizzle equations. This implements the standard
/// and z-order 2D single-sample equations, which cover the overwhelming
/// majority of color/UI textures; unknown modes are reported once and left
/// linear so nothing regresses.
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

    private static readonly HashSet<uint> _reportedModes = new();

    public static bool Enabled => _enabled;

    /// <summary>
    /// True when a surface with the given swizzle mode needs deswizzling.
    /// Mode 0 is linear and never needs it.
    /// </summary>
    public static bool NeedsDetile(uint swizzleMode) => _enabled && swizzleMode != 0;

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
        if (!_enabled || swizzleMode == 0 || elementsWide <= 0 || elementsHigh <= 0 || bytesPerElement <= 0)
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

        for (var y = 0; y < elementsHigh; y++)
        {
            for (var x = 0; x < elementsWide; x++)
            {
                var blockX = x / blockWidth;
                var blockY = y / blockHeight;
                var inBlockX = x % blockWidth;
                var inBlockY = y % blockHeight;

                var blockIndex = blockY * blocksPerRow + blockX;
                var withinBlock = kind == SwizzleKind.ZOrder
                    ? MortonInterleave((uint)inBlockX, (uint)inBlockY, blockWidth, blockHeight)
                    : StandardSwizzleOffset((uint)inBlockX, (uint)inBlockY, blockWidth, blockHeight);

                var sourceElement = (long)blockIndex * blockElements + withinBlock;
                var sourceByte = sourceElement * bytesPerElement;
                var destByte = ((long)y * elementsWide + x) * bytesPerElement;
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
        // GFX10 SWIZZLE_MODE enumeration (subset that appears for textures):
        //   1-4   = 256 B  Z/S/D/R
        //   5-8   = 4 KiB  Z/S/D/R
        //   9-12  = 64 KiB Z/S/D/R
        //   13-16 = 64 KiB _T (bank-swizzled) variants
        //   21-27 = 64 KiB _X (pipe-xor) variants
        // The pipe/bank XOR (_T/_X) affects which block a given tile lands in,
        // but the *within-block* element order matches the base S/Z equation,
        // which is what dominates visible correctness. We model the block
        // interior and treat blocks as linear-ordered.
        kind = SwizzleKind.Standard;
        blockBytes = 0;
        switch (swizzleMode)
        {
            case 1: case 5: case 9: case 13: case 21:
                kind = SwizzleKind.ZOrder;
                break;
            case 2: case 6: case 10: case 14: case 22:
            case 3: case 7: case 11: case 15: case 23:
            case 4: case 8: case 12: case 16: case 24:
            case 25: case 26: case 27:
                kind = SwizzleKind.Standard;
                break;
            default:
                return false;
        }

        blockBytes = swizzleMode switch
        {
            >= 1 and <= 4 => 256,
            >= 5 and <= 8 => 4096,
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
