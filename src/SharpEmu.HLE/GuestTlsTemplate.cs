// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Holds the main module's PT_TLS initialization image and derived static-TLS
/// block size so every guest thread can seed its thread-local storage with the
/// executable's real initial values instead of zeros.
///
/// Layout follows the x86-64 variant II ABI used by the PS4/PS5 (FreeBSD-like)
/// runtime: the thread pointer (guest FS base) points at the thread control
/// block, and the module's static TLS block sits immediately below it in the
/// range <c>[fsBase - BlockSize, fsBase)</c>. A thread-local at segment offset
/// <c>k</c> therefore lives at <c>fsBase - BlockSize + k</c>, which is exactly
/// what the TPOFF64 relocation and <c>__tls_get_addr</c> resolve to.
/// </summary>
public static class GuestTlsTemplate
{
    private static readonly object _gate = new();
    private static byte[] _initImage = [];
    private static ulong _blockSize;
    private static ulong _alignment = 1;

    /// <summary>Initialized bytes copied from the PT_TLS file image (tdata).</summary>
    public static byte[] InitImage
    {
        get { lock (_gate) { return _initImage; } }
    }

    /// <summary>
    /// Aligned size of the static TLS block (tdata + tbss rounded up to
    /// alignment). Zero when the module declares no PT_TLS segment, in which
    /// case callers keep their previous zero-TLS behavior.
    /// </summary>
    public static ulong BlockSize
    {
        get { lock (_gate) { return _blockSize; } }
    }

    /// <summary>Required alignment of the TLS block in bytes (power of two).</summary>
    public static ulong Alignment
    {
        get { lock (_gate) { return _alignment; } }
    }

    /// <summary>
    /// Records the main module's TLS template. <paramref name="memorySize"/> is
    /// the full tdata+tbss span; <paramref name="initImage"/> is the tdata bytes
    /// (may be shorter than memorySize, the remainder is implicitly zero).
    /// </summary>
    public static void Set(ReadOnlySpan<byte> initImage, ulong memorySize, ulong alignment)
    {
        var align = alignment == 0 ? 1 : alignment;
        var blockSize = (memorySize + align - 1) & ~(align - 1);
        lock (_gate)
        {
            _initImage = initImage.ToArray();
            _blockSize = blockSize;
            _alignment = align;
        }
    }

    /// <summary>Clears the template (called when a new process image loads).</summary>
    public static void Reset()
    {
        lock (_gate)
        {
            _initImage = [];
            _blockSize = 0;
            _alignment = 1;
        }
    }

    /// <summary>
    /// Seeds the static TLS block below <paramref name="threadPointer"/> with the
    /// template's initialized bytes. The mapped TLS region is already zero, so
    /// only the tdata image needs writing. No-op when no template is present.
    /// </summary>
    public static void SeedThreadBlock(CpuContext context, ulong threadPointer)
    {
        byte[] image;
        ulong blockSize;
        lock (_gate)
        {
            image = _initImage;
            blockSize = _blockSize;
        }

        if (blockSize == 0 || image.Length == 0 || threadPointer < blockSize)
        {
            return;
        }

        var blockBase = threadPointer - blockSize;
        context.Memory.TryWrite(blockBase, image);
    }
}
