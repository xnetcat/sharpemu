// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Host-side decoder behind a single AJM instance.
/// </summary>
/// <remarks>
/// AJM is a streaming interface: the guest hands over compressed data in small pieces and
/// expects PCM back in the codec's native frame layout. SharpEmu satisfies that with a
/// long-lived FFmpeg process per instance — the same binary AvPlayer already depends on —
/// fed through stdin and drained from stdout by a reader thread.
///
/// A session that reports <see cref="IsUsable"/> as <see langword="false"/> is a placeholder
/// for a codec SharpEmu cannot decode. It never fabricates PCM; the caller routes those jobs
/// down an explicitly logged silence path instead.
/// </remarks>
internal sealed class AjmDecoderSession : IDisposable
{
    /// <summary>Ceiling on simultaneous decoder processes, so a voice-heavy title cannot fork-bomb the host.</summary>
    private const int MaxConcurrentSessions = 48;

    /// <summary>Upper bound on buffered PCM before the reader thread starts dropping.</summary>
    private const int MaxBufferedPcmBytes = 4 << 20;

    /// <summary>Upper bound on compressed bytes queued towards FFmpeg's stdin.</summary>
    private const int MaxQueuedInputBytes = 2 << 20;

    private static int _activeSessions;

    // Two locks, and never taken in the other order. Writing to FFmpeg's stdin can block for
    // as long as FFmpeg is behind, and FFmpeg only drains stdin while something is draining
    // its stdout — so the reader thread must be able to bank PCM without waiting on whoever
    // is mid-write. Holding one lock across both would deadlock the pair.
    private readonly object _processGate = new();
    private readonly object _pcmGate = new();
    private readonly uint _codecType;
    private readonly int _channels;
    private readonly AjmFormatEncoding _encoding;
    private readonly byte[]? _configData;
    private readonly byte[] _streamHeader;
    private readonly string _inputFormat;
    private readonly string _ffmpegPath;

    private Process? _process;
    private Thread? _reader;

    /// <summary>
    /// Bumped every time a process is started. A reader thread whose generation is stale
    /// belongs to a torn-down process and must not bank PCM into the new stream's buffer.
    /// </summary>
    private int _generation;

    private byte[] _pcm = [];
    private int _pcmStart;
    private int _pcmEnd;
    private bool _counted;
    private volatile bool _faulted;
    private volatile bool _disposed;

    private AjmDecoderSession(
        string ffmpegPath,
        uint codecType,
        int channels,
        AjmFormatEncoding encoding,
        byte[]? configData,
        string inputFormat,
        byte[] streamHeader)
    {
        _ffmpegPath = ffmpegPath;
        _codecType = codecType;
        _channels = channels;
        _encoding = encoding;
        _configData = configData;
        _inputFormat = inputFormat;
        _streamHeader = streamHeader;
        IsUsable = true;
    }

    private AjmDecoderSession(string unsupportedReason)
    {
        _ffmpegPath = string.Empty;
        _inputFormat = string.Empty;
        _streamHeader = [];
        UnsupportedReason = unsupportedReason;
        IsUsable = false;
    }

    /// <summary>Whether this session can actually decode. False marks a codec SharpEmu does not support.</summary>
    public bool IsUsable { get; }

    public string? UnsupportedReason { get; }

    /// <summary>Sample rate reported by the codec configuration, or 0 when unknown.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Bytes of compressed data that make up one decodable unit, or 0 when variable.</summary>
    public int FrameBytes { get; private set; }

    /// <summary>PCM samples produced per decoded frame, or 0 when unknown.</summary>
    public int FrameSamples { get; private set; }

    public static AjmDecoderSession Unsupported(string reason) => new(reason);

    /// <summary>
    /// Builds a decoder for an AJM instance, or explains why the codec cannot be decoded.
    /// </summary>
    public static AjmDecoderSession? TryCreate(
        uint codecType,
        int channels,
        AjmFormatEncoding encoding,
        byte[]? configData,
        out string reason)
    {
        if (!IsDecodeEnabled())
        {
            reason = "disabled by SHARPEMU_AJM_DECODE=0";
            return null;
        }

        if (Volatile.Read(ref _activeSessions) >= MaxConcurrentSessions)
        {
            reason = $"decoder process budget exhausted ({MaxConcurrentSessions} live instances)";
            return null;
        }

        // Resolve the codec first so an unsupported codec or a malformed configuration is
        // reported as such, whether or not the host happens to have FFmpeg installed.
        string inputFormat;
        byte[] streamHeader = [];
        Atrac9Config atrac9 = default;
        switch (codecType)
        {
            case AjmCodecType.Mp3Dec:
                inputFormat = "mp3";
                break;

            case AjmCodecType.M4aacDec:
                inputFormat = "aac";
                break;

            case AjmCodecType.At9Dec:
                if (configData is null || configData.Length < Atrac9ConfigDataBytes)
                {
                    reason = "ATRAC9 instance decoded before sceAjmBatchJobInitialize supplied config data";
                    return null;
                }

                if (!Atrac9Config.TryParse(configData.AsSpan(0, Atrac9ConfigDataBytes), out atrac9))
                {
                    reason = "unparsable ATRAC9 config data " +
                             Convert.ToHexString(configData.AsSpan(0, Atrac9ConfigDataBytes));
                    return null;
                }

                inputFormat = "wav";
                streamHeader = BuildAtrac9RiffHeader(atrac9, configData.AsSpan(0, Atrac9ConfigDataBytes));
                break;

            default:
                reason = $"AJM codec type {codecType} has no SharpEmu decoder";
                return null;
        }

        var ffmpeg = AvPlayerExports.FindFfmpeg();
        if (ffmpeg is null)
        {
            reason = "ffmpeg binary not found (set SHARPEMU_FFMPEG_PATH)";
            return null;
        }

        reason = string.Empty;
        return new AjmDecoderSession(ffmpeg, codecType, channels, encoding, configData, inputFormat, streamHeader)
        {
            SampleRate = atrac9.SampleRate,
            FrameBytes = atrac9.SuperframeBytes,
            FrameSamples = atrac9.SuperframeSamples,
        };
    }

    /// <summary>
    /// Pushes compressed data into the decoder and copies whatever PCM is ready into
    /// <paramref name="output"/>.
    /// </summary>
    /// <returns>Bytes of PCM written.</returns>
    public int Decode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        out int inputConsumed,
        out int framesDecoded)
    {
        inputConsumed = 0;
        framesDecoded = 0;
        if (!IsUsable || output.Length == 0)
        {
            return 0;
        }

        byte[]? pending = null;
        if (input.Length != 0)
        {
            pending = input.ToArray();
        }

        lock (_processGate)
        {
            if (_faulted || _disposed || !EnsureProcess())
            {
                return 0;
            }

            if (pending is not null && TryWriteInput(pending))
            {
                inputConsumed = pending.Length;
            }
        }

        var written = DrainPcm(output);
        if (written != 0 && FrameSamples > 0)
        {
            var bytesPerFrame = FrameSamples * ChannelCount * BytesPerSample;
            framesDecoded = bytesPerFrame > 0 ? Math.Max(1, written / bytesPerFrame) : 1;
        }
        else if (written != 0)
        {
            framesDecoded = 1;
        }

        return written;
    }

    /// <summary>
    /// Drops all decoder state. AJM resets an instance when a voice loops or is re-pooled, and
    /// carrying FFmpeg's stream state across that boundary would decode the new stream against
    /// the old one's history.
    /// </summary>
    public void Reset()
    {
        lock (_processGate)
        {
            StopProcess();
            _faulted = false;
        }

        DiscardPcm();
    }

    public void Dispose()
    {
        lock (_processGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopProcess();
        }

        DiscardPcm();
    }

    private void DiscardPcm()
    {
        lock (_pcmGate)
        {
            _pcmStart = 0;
            _pcmEnd = 0;
            Monitor.PulseAll(_pcmGate);
        }
    }

    private int ChannelCount => _channels > 0 ? _channels : 2;

    private int BytesPerSample => _encoding == AjmFormatEncoding.S16 ? 2 : 4;

    private string OutputFormat => _encoding switch
    {
        AjmFormatEncoding.S32 => "s32le",
        AjmFormatEncoding.Float => "f32le",
        _ => "s16le",
    };

    private static bool IsDecodeEnabled() =>
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_AJM_DECODE"), "0", StringComparison.Ordinal);

    private bool EnsureProcess()
    {
        if (_process is { HasExited: false })
        {
            return true;
        }

        if (_process is not null)
        {
            StopProcess();
        }

        if (Interlocked.Increment(ref _activeSessions) > MaxConcurrentSessions)
        {
            Interlocked.Decrement(ref _activeSessions);
            _faulted = true;
            Console.Error.WriteLine(
                "[LOADER][WARN] ajm.decoder_budget_exhausted codec=" +
                $"{AjmCodecType.Name(_codecType)} (instance falls back to silence)");
            return false;
        }

        _counted = true;

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        // Keep FFmpeg's probe as small as it will go: the input is a pipe fed one codec frame
        // at a time, and every byte it buffers before emitting is added decode latency.
        startInfo.ArgumentList.Add("-probesize");
        startInfo.ArgumentList.Add("32");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(_inputFormat);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(OutputFormat);
        if (_channels > 0)
        {
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(_channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add("-flush_packets");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception error)
        {
            _process = null;
            Console.Error.WriteLine(
                $"[LOADER][WARN] ajm.decoder_start_failed codec={AjmCodecType.Name(_codecType)} error={error.Message}");
        }

        if (_process is null)
        {
            ReleaseSlot();
            _faulted = true;
            return false;
        }

        lock (_pcmGate)
        {
            _pcm = new byte[64 << 10];
            _pcmStart = 0;
            _pcmEnd = 0;
        }

        var process = _process;
        var generation = Interlocked.Increment(ref _generation);
        _reader = new Thread(() => ReadLoop(process, generation))
        {
            IsBackground = true,
            Name = "ajm-decoder",
        };
        _reader.Start();

        // Drain stderr so a chatty codec cannot fill the pipe and wedge FFmpeg.
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] ajm.decoder_stderr codec={AjmCodecType.Name(_codecType)} {text.Trim()}");
                }
            }
            catch (Exception)
            {
                // The process exited while stderr was being drained; nothing to report.
            }
        });

        if (_streamHeader.Length != 0 && !TryWriteInput(_streamHeader))
        {
            return false;
        }

        return true;
    }

    private bool TryWriteInput(byte[] bytes)
    {
        if (_process is not { HasExited: false } process || bytes.Length > MaxQueuedInputBytes)
        {
            return false;
        }

        try
        {
            process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            process.StandardInput.BaseStream.Flush();
            return true;
        }
        catch (Exception error)
        {
            _faulted = true;
            Console.Error.WriteLine(
                $"[LOADER][WARN] ajm.decoder_write_failed codec={AjmCodecType.Name(_codecType)} error={error.Message}");
            return false;
        }
    }

    private void ReadLoop(Process process, int generation)
    {
        var buffer = new byte[16 << 10];
        try
        {
            while (true)
            {
                var read = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                if (Volatile.Read(ref _generation) != generation)
                {
                    return;
                }

                lock (_pcmGate)
                {
                    Append(buffer.AsSpan(0, read));
                    Monitor.PulseAll(_pcmGate);
                }
            }
        }
        catch (Exception)
        {
            // The process was torn down (reset, dispose, or crash); the next Decode call
            // notices HasExited and restarts it.
        }
        finally
        {
            // Wake anyone parked in DrainPcm: no more PCM is coming from this process.
            lock (_pcmGate)
            {
                Monitor.PulseAll(_pcmGate);
            }
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_pcmStart == _pcmEnd)
        {
            _pcmStart = 0;
            _pcmEnd = 0;
        }

        if (_pcmEnd + bytes.Length > _pcm.Length)
        {
            var live = _pcmEnd - _pcmStart;
            if (live + bytes.Length <= _pcm.Length)
            {
                Array.Copy(_pcm, _pcmStart, _pcm, 0, live);
            }
            else
            {
                var capacity = Math.Min(MaxBufferedPcmBytes, Math.Max(_pcm.Length * 2, live + bytes.Length));
                if (capacity < live + bytes.Length)
                {
                    // The guest is not consuming PCM fast enough. Dropping the oldest samples
                    // keeps playback live rather than stalling the decoder behind a full pipe.
                    var drop = live + bytes.Length - capacity;
                    _pcmStart += drop;
                    live -= drop;
                }

                var grown = new byte[capacity];
                Array.Copy(_pcm, _pcmStart, grown, 0, live);
                _pcm = grown;
            }

            _pcmStart = 0;
            _pcmEnd = live;
        }

        bytes.CopyTo(_pcm.AsSpan(_pcmEnd));
        _pcmEnd += bytes.Length;
    }

    /// <summary>
    /// Copies ready PCM out, waiting briefly for the decode pipeline to catch up. The wait is
    /// bounded because this runs on the title's audio thread.
    /// </summary>
    private int DrainPcm(Span<byte> output)
    {
        var deadline = Environment.TickCount64 + WaitMilliseconds;
        lock (_pcmGate)
        {
            while (true)
            {
                var available = _pcmEnd - _pcmStart;
                if (available > 0)
                {
                    var count = Math.Min(available, output.Length);
                    _pcm.AsSpan(_pcmStart, count).CopyTo(output);
                    _pcmStart += count;
                    if (_pcmStart == _pcmEnd)
                    {
                        _pcmStart = 0;
                        _pcmEnd = 0;
                    }

                    return count;
                }

                if (_disposed || _faulted)
                {
                    return 0;
                }

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !Monitor.Wait(_pcmGate, (int)remaining))
                {
                    return 0;
                }
            }
        }
    }

    private static int WaitMilliseconds
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SHARPEMU_AJM_DECODE_WAIT_MS");
            return int.TryParse(configured, out var value) && value is >= 0 and <= 1000 ? value : 20;
        }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _reader = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.BaseStream.Close();
                if (!process.WaitForExit(200))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
            ReleaseSlot();
        }
    }

    private void ReleaseSlot()
    {
        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref _activeSessions);
        }
    }

    internal const int Atrac9ConfigDataBytes = 4;

    private static readonly byte[] Atrac9SubFormatGuid =
    [
        0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D,
        0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C,
    ];

    /// <summary>
    /// Wraps a raw ATRAC9 superframe stream in the RIFF/WAVE header FFmpeg's ATRAC9 decoder
    /// expects. The guest never sends a container — it hands AJM the four config bytes at
    /// initialize time and bare superframes afterwards — so the header is synthesized here.
    /// The data chunk is declared open-ended because the stream is a pipe with no known length.
    /// </summary>
    internal static byte[] BuildAtrac9RiffHeader(Atrac9Config config, ReadOnlySpan<byte> configData)
    {
        const int FormatChunkBytes = 52;
        var header = new byte[12 + 8 + FormatChunkBytes + 8];
        var cursor = header.AsSpan();

        "RIFF"u8.CopyTo(cursor);
        BinaryPrimitives.WriteUInt32LittleEndian(cursor[4..], 0x7FFF_FFFF);
        "WAVE"u8.CopyTo(cursor[8..]);
        "fmt "u8.CopyTo(cursor[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(cursor[16..], FormatChunkBytes);

        var format = cursor[20..];
        // WAVE_FORMAT_EXTENSIBLE; the ATRAC9 sub-format GUID selects the decoder.
        BinaryPrimitives.WriteUInt16LittleEndian(format, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(format[2..], (ushort)config.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format[4..], (uint)config.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            format[8..],
            (uint)(config.SuperframeSamples == 0
                ? 0
                : (long)config.SuperframeBytes * config.SampleRate / config.SuperframeSamples));
        // block_align must be the superframe size: it is what FFmpeg packetizes on.
        BinaryPrimitives.WriteUInt16LittleEndian(format[12..], (ushort)config.SuperframeBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(format[14..], 0);
        // cbSize covers the extensible tail plus the 12 bytes of ATRAC9 extradata.
        BinaryPrimitives.WriteUInt16LittleEndian(format[16..], 34);
        BinaryPrimitives.WriteUInt16LittleEndian(format[18..], (ushort)config.SuperframeSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(format[20..], ChannelMask(config.Channels));
        Atrac9SubFormatGuid.CopyTo(format[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(format[40..], 1);
        configData[..Atrac9ConfigDataBytes].CopyTo(format[44..]);
        BinaryPrimitives.WriteUInt32LittleEndian(format[48..], 0);

        "data"u8.CopyTo(cursor[(20 + FormatChunkBytes)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(cursor[(24 + FormatChunkBytes)..], 0x7FFF_FFF0);
        return header;
    }

    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        6 => 0x3F,
        8 => 0x63F,
        _ => 0,
    };
}

/// <summary>
/// The four ATRAC9 configuration bytes a title lifts out of its stream's RIFF header and hands
/// to <c>sceAjmBatchJobInitialize</c>. Everything needed to frame the bitstream — sample rate,
/// channel count, frame size and superframe grouping — is packed into them.
/// </summary>
internal readonly record struct Atrac9Config(
    int SampleRate,
    int Channels,
    int FrameBytes,
    int FramesInSuperframe,
    int FrameSamples)
{
    public int SuperframeBytes => FrameBytes * FramesInSuperframe;

    public int SuperframeSamples => FrameSamples * FramesInSuperframe;

    private static readonly int[] SampleRates =
    [
        11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
        64000, 88200, 96000, 128000, 176400, 192000,
    ];

    private static readonly int[] ChannelCounts = [1, 2, 2, 6, 8, 4];

    // Frame length in samples is 2^n where n comes from the sample-rate index.
    private static readonly int[] FrameSamplesPower = [6, 6, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8];

    /// <summary>
    /// Decodes the packed configuration word. The layout is a big-endian bit stream:
    /// 8-bit sync (0xFE), 4-bit sample-rate index, 3-bit channel-config index, 1 reserved bit,
    /// 11-bit frame size minus one, then a 2-bit superframe index.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> configData, out Atrac9Config config)
    {
        config = default;
        if (configData.Length < 4 || configData[0] != 0xFE)
        {
            return false;
        }

        var word = ((uint)configData[1] << 16) | ((uint)configData[2] << 8) | configData[3];
        var sampleRateIndex = (int)((word >> 20) & 0xF);
        var channelConfigIndex = (int)((word >> 17) & 0x7);
        var frameBytes = (int)((word >> 5) & 0x7FF) + 1;
        var superframeIndex = (int)((word >> 3) & 0x3);

        if (sampleRateIndex >= SampleRates.Length || channelConfigIndex >= ChannelCounts.Length)
        {
            return false;
        }

        config = new Atrac9Config(
            SampleRates[sampleRateIndex],
            ChannelCounts[channelConfigIndex],
            frameBytes,
            1 << superframeIndex,
            1 << FrameSamplesPower[sampleRateIndex]);
        return true;
    }
}
