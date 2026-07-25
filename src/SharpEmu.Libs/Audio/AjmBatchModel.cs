// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Audio;

/// <summary>
/// AJM result bits reported through <c>SceAjmSidebandResult.result</c>. These are per-job
/// status flags, not API return codes: a job can report several at once and the batch call
/// itself still returns success.
/// </summary>
internal static class AjmJobResult
{
    public const int NotInitialized = 0x0000_0001;
    public const int InvalidData = 0x0000_0002;
    public const int InvalidParameter = 0x0000_0004;
    public const int PartialInput = 0x0000_0008;
    public const int NotEnoughRoom = 0x0000_0010;
    public const int StreamChange = 0x0000_0020;
    public const int TooManyChannels = 0x0000_0040;
    public const int UnsupportedFlag = 0x0000_0080;
    public const int SidebandTruncated = 0x0000_0100;
    public const int PriorityPassed = 0x0000_0200;
    public const int CodecError = 0x4000_0000;
    public const int Fatal = unchecked((int)0x8000_0000);
}

/// <summary>Codec ids accepted by <c>sceAjmModuleRegister</c> / <c>sceAjmInstanceCreate</c>.</summary>
internal static class AjmCodecType
{
    public const uint Mp3Dec = 0;
    public const uint At9Dec = 1;
    public const uint M4aacDec = 2;

    public static string Name(uint codecType) => codecType switch
    {
        Mp3Dec => "mp3",
        At9Dec => "at9",
        M4aacDec => "m4aac",
        _ => $"codec{codecType}",
    };
}

/// <summary>PCM encodings an AJM instance can be asked to emit.</summary>
internal enum AjmFormatEncoding
{
    S16 = 0,
    S32 = 1,
    Float = 2,
}

/// <summary>
/// The kinds of job a guest can append to a batch. Each maps to one of the
/// <c>sceAjmBatchJob*</c> entry points.
/// </summary>
internal enum AjmJobKind
{
    Decode,
    DecodeSplit,
    Initialize,
    ClearContext,
    SetGaplessDecode,
    GetResampleInfo,
    SetResampleParameters,
    GetStatistics,
}

/// <summary>A guest (address, size) pair, as used by the split decode descriptor arrays.</summary>
internal readonly record struct AjmGuestBuffer(ulong Address, ulong Size);

/// <summary>
/// One queued batch job. AJM batches are built by the guest through a sequence of
/// <c>sceAjmBatchJob*</c> calls and only executed at <c>sceAjmBatchStart</c>, so the job
/// description is recorded here and replayed later.
/// </summary>
internal sealed class AjmJob
{
    public AjmJobKind Kind { get; init; }

    public uint InstanceId { get; init; }

    /// <summary>Raw job flags, when the entry point carries them.</summary>
    public ulong Flags { get; init; }

    /// <summary>Compressed input buffers (one entry for the non-split decode path).</summary>
    public IReadOnlyList<AjmGuestBuffer> InputBuffers { get; init; } = [];

    /// <summary>PCM output buffers (one entry for the non-split decode path).</summary>
    public IReadOnlyList<AjmGuestBuffer> OutputBuffers { get; init; } = [];

    /// <summary>Sideband input block (codec init parameters, gapless or resample settings).</summary>
    public AjmGuestBuffer SidebandInput { get; init; }

    /// <summary>Sideband output block, always beginning with <c>SceAjmSidebandResult</c>.</summary>
    public AjmGuestBuffer SidebandOutput { get; init; }

    public ulong TotalInputSize
    {
        get
        {
            ulong total = 0;
            foreach (var buffer in InputBuffers)
            {
                total += buffer.Size;
            }

            return total;
        }
    }

    public ulong TotalOutputSize
    {
        get
        {
            ulong total = 0;
            foreach (var buffer in OutputBuffers)
            {
                total += buffer.Size;
            }

            return total;
        }
    }
}
