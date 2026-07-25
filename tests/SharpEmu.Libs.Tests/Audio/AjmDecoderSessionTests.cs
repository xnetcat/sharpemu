// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// Covers the host decoder behind an AJM instance: ATRAC9 stream framing derived from the
/// four config bytes a title passes to <c>sceAjmBatchJobInitialize</c>, the container header
/// synthesized for it, and an end-to-end decode of a real compressed stream.
/// </summary>
[Collection(AjmStateCollection.Name)]
public sealed class AjmDecoderSessionTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x10;
    private const ulong InstanceAddress = MemoryBase + 0x20;
    private const ulong BatchIdAddress = MemoryBase + 0x30;
    private const ulong BatchInfoAddress = MemoryBase + 0x100;
    private const ulong BatchBufferAddress = MemoryBase + 0x200;
    private const ulong SidebandAddress = MemoryBase + 0x1000;
    private const ulong StackAddress = MemoryBase + 0x8000;
    private const ulong InputAddress = MemoryBase + 0x10000;
    private const ulong OutputAddress = MemoryBase + 0x40000;

    private readonly AllocatingFakeCpuMemory _memory = new(MemoryBase, 0x100000);
    private readonly CpuContext _ctx;

    public AjmDecoderSessionTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = StackAddress,
        };
    }

    public void Dispose() => AjmExports.ResetForTests();

    [Fact]
    public void Atrac9Config_DecodesTheConfigWordShippedInPs5At9Streams()
    {
        // The config data from PPSA10112's sce_sys/snd0.at9: 48 kHz stereo, 128-byte frames
        // grouped four to a superframe.
        Assert.True(Atrac9Config.TryParse([0xFE, 0x74, 0x0F, 0xF0], out var config));

        Assert.Equal(48000, config.SampleRate);
        Assert.Equal(2, config.Channels);
        Assert.Equal(128, config.FrameBytes);
        Assert.Equal(4, config.FramesInSuperframe);
        Assert.Equal(256, config.FrameSamples);
        Assert.Equal(512, config.SuperframeBytes);
        Assert.Equal(1024, config.SuperframeSamples);
    }

    [Theory]
    // Wrong sync byte.
    [InlineData(new byte[] { 0x00, 0x74, 0x0F, 0xF0 })]
    // Truncated.
    [InlineData(new byte[] { 0xFE, 0x74 })]
    // Sample-rate index 15 is not a defined rate.
    [InlineData(new byte[] { 0xFE, 0xF4, 0x0F, 0xF0 })]
    public void Atrac9Config_RejectsMalformedConfigData(byte[] configData) =>
        Assert.False(Atrac9Config.TryParse(configData, out _));

    [Fact]
    public void Atrac9RiffHeader_MatchesTheLayoutFfmpegExpects()
    {
        Assert.True(Atrac9Config.TryParse([0xFE, 0x74, 0x0F, 0xF0], out var config));
        var header = AjmDecoderSession.BuildAtrac9RiffHeader(config, [0xFE, 0x74, 0x0F, 0xF0]);

        Assert.Equal("RIFF"u8.ToArray(), header[0..4]);
        Assert.Equal("WAVE"u8.ToArray(), header[8..12]);
        Assert.Equal("fmt "u8.ToArray(), header[12..16]);
        Assert.Equal(52u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));

        var format = header.AsSpan(20);
        Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(format));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(format[2..]));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(format[4..]));
        // block_align must be the superframe size — FFmpeg packetizes the stream on it.
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(format[12..]));
        // cbSize covers the extensible tail plus exactly 12 bytes of ATRAC9 extradata, which
        // is the size FFmpeg's atrac9 decoder validates.
        Assert.Equal(34, BinaryPrimitives.ReadUInt16LittleEndian(format[16..]));
        Assert.Equal(1024, BinaryPrimitives.ReadUInt16LittleEndian(format[18..]));

        var guid = format[24..40].ToArray();
        Assert.Equal(
            new byte[]
            {
                0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D,
                0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C,
            },
            guid);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(format[40..]));
        Assert.Equal(new byte[] { 0xFE, 0x74, 0x0F, 0xF0 }, format[44..48].ToArray());

        Assert.Equal("data"u8.ToArray(), header[72..76]);
        Assert.Equal(80, header.Length);
    }

    [Fact]
    public void TryCreate_RefusesAt9BeforeInitializeSuppliedConfigData()
    {
        var session = AjmDecoderSession.TryCreate(
            codecType: 1,
            channels: 2,
            AjmFormatEncoding.S16,
            configData: null,
            out var reason);

        Assert.Null(session);
        Assert.Contains("config data", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RefusesAt9WithUnparsableConfigData()
    {
        var session = AjmDecoderSession.TryCreate(
            codecType: 1,
            channels: 2,
            AjmFormatEncoding.S16,
            configData: [0x00, 0x00, 0x00, 0x00],
            out var reason);

        Assert.Null(session);
        Assert.Contains("unparsable", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_NamesTheCodecItCannotDecode()
    {
        var session = AjmDecoderSession.TryCreate(
            codecType: 7,
            channels: 2,
            AjmFormatEncoding.S16,
            configData: null,
            out var reason);

        Assert.Null(session);
        Assert.Contains("no SharpEmu decoder", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedSession_NeverFabricatesPcm()
    {
        using var session = AjmDecoderSession.Unsupported("test");
        var output = new byte[64];

        Assert.False(session.IsUsable);
        Assert.Equal(0, session.Decode([1, 2, 3, 4], output, out var consumed, out var frames));
        Assert.Equal(0, consumed);
        Assert.Equal(0, frames);
    }

    [Fact]
    public void Mp3Instance_DecodesRealPcmThroughTheBatchPath()
    {
        var encoded = TryEncodeSine("libmp3lame", "mp3");
        if (encoded is null)
        {
            // No MP3 encoder on this host to synthesize a fixture with. Pin the documented
            // fallback instead of letting the test quietly disappear.
            AssertSilenceFallback(codecType: 0);
            return;
        }

        var pcm = DecodeThroughAjm(codecType: 0, encoded);

        // A 440 Hz sine has to come back as something audibly non-zero.
        Assert.True(pcm.Length > 0, "AJM produced no PCM for an MP3 instance");
        Assert.True(PeakAmplitude(pcm) > 1000, $"decoded PCM was near-silent (peak {PeakAmplitude(pcm)})");
    }

    [Fact]
    public void AacInstance_DecodesRealPcmThroughTheBatchPath()
    {
        var encoded = TryEncodeSine("aac", "adts");
        if (encoded is null)
        {
            AssertSilenceFallback(codecType: 2);
            return;
        }

        var pcm = DecodeThroughAjm(codecType: 2, encoded);

        Assert.True(pcm.Length > 0, "AJM produced no PCM for an AAC instance");
        Assert.True(PeakAmplitude(pcm) > 1000, $"decoded PCM was near-silent (peak {PeakAmplitude(pcm)})");
    }

    /// <summary>
    /// Asserts the contract SharpEmu falls back to when no decoder can be built: the guest's
    /// PCM buffer is zero-filled and the input is reported as fully consumed, so the voice
    /// stays in sync and the title keeps advancing its bitstream.
    /// </summary>
    private void AssertSilenceFallback(uint codecType)
    {
        var pcm = DecodeThroughAjm(codecType, [0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Assert.All(pcm, sample => Assert.Equal(0, sample));
    }

    /// <summary>
    /// Drives a compressed stream through the public AJM batch surface exactly as a title
    /// would — register codec, create instance, queue a decode job, start the batch — and
    /// returns the PCM the guest buffer received.
    /// </summary>
    private byte[] DecodeThroughAjm(uint codecType, byte[] encoded)
    {
        const int OutputBytes = 0x20000;

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        var contextId = ReadUInt32(ContextAddress);

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AjmExports.AjmModuleRegister(_ctx));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        // version=1, channels=2, format=S16.
        _ctx[CpuRegister.Rdx] = 1 | (2 << 3);
        _ctx[CpuRegister.Rcx] = InstanceAddress;
        Assert.Equal(0, AjmExports.AjmInstanceCreate(_ctx));
        var instanceId = ReadUInt32(InstanceAddress);

        Assert.True(_memory.TryWrite(InputAddress, encoded));

        var collected = new List<byte>();
        // FFmpeg buffers a fixed amount of compressed input before emitting its first frame,
        // so a title's early decode jobs legitimately come back short. Submit the stream and
        // then keep draining, which is exactly what a streaming voice does.
        for (var attempt = 0; attempt < 40 && collected.Count == 0; attempt++)
        {
            WriteBatchInfo();
            WriteStackArg(0, SidebandAddress);
            WriteStackArg(1, 32);
            _ctx[CpuRegister.Rdi] = BatchInfoAddress;
            _ctx[CpuRegister.Rsi] = instanceId;
            _ctx[CpuRegister.Rdx] = attempt == 0 ? InputAddress : 0;
            _ctx[CpuRegister.Rcx] = attempt == 0 ? (ulong)encoded.Length : 0;
            _ctx[CpuRegister.R8] = OutputAddress;
            _ctx[CpuRegister.R9] = OutputBytes;
            Assert.Equal(0, AjmExports.AjmBatchJobDecode(_ctx));

            _ctx[CpuRegister.Rdi] = contextId;
            _ctx[CpuRegister.Rsi] = BatchInfoAddress;
            _ctx[CpuRegister.Rdx] = 0;
            _ctx[CpuRegister.Rcx] = 0;
            _ctx[CpuRegister.R8] = BatchIdAddress;
            Assert.Equal(0, AjmExports.AjmBatchStart(_ctx));

            var written = ReadInt32(SidebandAddress + 12);
            if (written > 0)
            {
                var pcm = new byte[written];
                Assert.True(_memory.TryRead(OutputAddress, pcm));
                collected.AddRange(pcm);
            }
        }

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = instanceId;
        Assert.Equal(0, AjmExports.AjmInstanceDestroy(_ctx));
        return [.. collected];
    }

    private static int PeakAmplitude(byte[] pcm)
    {
        var peak = 0;
        for (var offset = 0; offset + 1 < pcm.Length; offset += 2)
        {
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset))));
        }

        return peak;
    }

    /// <summary>
    /// Synthesizes a short 440 Hz test tone with FFmpeg's <c>lavfi</c> source. Returns
    /// <see langword="null"/> when FFmpeg or the requested encoder is unavailable, so the
    /// decode tests skip instead of failing on a host without them.
    /// </summary>
    private static byte[]? TryEncodeSine(string encoder, string muxer)
    {
        var ffmpeg = AvPlayerExports.FindFfmpeg();
        if (ffmpeg is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=48000",
                     "-ac", "2", "-c:a", encoder, "-b:a", "128k", "-f", muxer, "pipe:1",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000) || process.ExitCode != 0 || buffer.Length == 0)
            {
                return null;
            }

            return buffer.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteBatchInfo()
    {
        WriteUInt64(BatchInfoAddress, BatchBufferAddress);
        WriteUInt64(BatchInfoAddress + 8, 0);
        WriteUInt64(BatchInfoAddress + 16, 0x800);
        WriteUInt64(BatchInfoAddress + 24, 0);
    }

    private void WriteStackArg(int index, ulong value) =>
        WriteUInt64(StackAddress + 8 + ((ulong)index * 8), value);

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private int ReadInt32(ulong address) => unchecked((int)ReadUInt32(address));
}
