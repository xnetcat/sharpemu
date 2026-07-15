// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// Registry of live <see cref="AvPlayerSession"/> instances keyed by the opaque
/// guest handle returned from sceAvPlayerInit(Ex). One session drives one movie
/// at a time through the ffmpeg CLI used as an out-of-process decoder.
/// </summary>
internal static class AvPlayerSessions
{
    private static readonly ConcurrentDictionary<ulong, AvPlayerSession> _sessions = new();

    /// <summary>
    /// True unless the caller opted out of real playback via
    /// SHARPEMU_AVPLAYER_SKIP_PLAYBACK=1 or SHARPEMU_AVPLAYER_FFMPEG=0, in which
    /// case the exports keep the historical "no decoder present" behaviour.
    /// </summary>
    public static bool PlaybackEnabled { get; } =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_AVPLAYER_SKIP_PLAYBACK"), "1", StringComparison.Ordinal) &&
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_AVPLAYER_FFMPEG"), "0", StringComparison.Ordinal);

    public static AvPlayerSession Create(ulong handle)
    {
        var session = new AvPlayerSession(handle);
        _sessions[handle] = session;
        return session;
    }

    public static bool TryGet(ulong handle, out AvPlayerSession session) =>
        _sessions.TryGetValue(handle, out session!);

    public static bool Remove(ulong handle, out AvPlayerSession session)
    {
        if (_sessions.TryRemove(handle, out session!))
        {
            return true;
        }

        session = null!;
        return false;
    }
}

/// <summary>
/// One AvPlayer instance. Video and audio are decoded by two ffmpeg child
/// processes (raw NV12 frames and interleaved signed-16 stereo PCM), read from
/// their stdout pipes on demand and paced against a wall-clock stopwatch that is
/// started at sceAvPlayerStart. Decoded frames are copied into host-backed guest
/// buffers so the guest receives real guest-addressable pointers without the HLE
/// layer ever calling the guest's own memory-replacement allocator.
/// </summary>
internal sealed class AvPlayerSession : IDisposable
{
    private const int AudioSampleRate = 48000;
    private const int AudioChannels = 2;
    private const int AudioSamplesPerChunk = 1024;
    private const int AudioBytesPerSample = sizeof(short);
    private const int AudioChunkBytes = AudioSamplesPerChunk * AudioChannels * AudioBytesPerSample;

    private readonly object _gate = new();
    private readonly ulong _handle;
    private readonly Stopwatch _clock = new();
    private readonly byte[] _audioHost = new byte[AudioChunkBytes];

    private string? _hostPath;
    private bool _probed;
    private bool _hasVideo;
    private bool _hasAudio;
    private int _width;
    private int _height;
    private int _frameSize;
    private double _fps = 30.0;
    private double _durationMs;

    private bool _looping;
    private bool _started;
    private bool _videoFinished;

    private Process? _videoProcess;
    private Process? _audioProcess;
    private Stream? _videoStdout;
    private Stream? _audioStdout;
    private byte[]? _videoHost;

    private ulong _guestVideo;
    private int _guestVideoCapacity;
    private ulong _guestAudio;

    private long _videoFrameIndex;
    private long _lastTimestampMs;

    public AvPlayerSession(ulong handle) => _handle = handle;

    /// <summary>
    /// Resolves and probes the guest source path. Returns false when the file
    /// cannot be probed (missing ffprobe, unreadable file), letting the caller
    /// fall back to the "finished immediately" behaviour so the guest advances
    /// instead of waiting forever for frames that will never arrive.
    /// </summary>
    public bool AddSource(string guestPath)
    {
        var host = ResolveHostPath(guestPath);
        if (host is null || !File.Exists(host))
        {
            Trace($"add_source guest='{guestPath}' host='{host}' missing");
            return false;
        }

        lock (_gate)
        {
            _hostPath = host;
            _probed = Probe(host);
            Trace($"add_source guest='{guestPath}' host='{host}' probed={_probed} " +
                  $"video={_hasVideo} audio={_hasAudio} {_width}x{_height} fps={_fps:F3} dur_ms={_durationMs:F0}");
            return _probed;
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_hostPath is null || !_probed)
            {
                return false;
            }

            StopStreamsLocked();
            if (!SpawnStreamsLocked())
            {
                return false;
            }

            _videoFrameIndex = 0;
            _lastTimestampMs = 0;
            _videoFinished = false;
            _started = true;
            _clock.Restart();
            Trace($"start handle=0x{_handle:X}");
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopStreamsLocked();
            _started = false;
            _videoFinished = false;
            _videoFrameIndex = 0;
            _clock.Reset();
            Trace($"stop handle=0x{_handle:X}");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_started && _clock.IsRunning)
            {
                _clock.Stop();
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_started && !_clock.IsRunning && !_videoFinished)
            {
                _clock.Start();
            }
        }
    }

    public void SetLooping(bool looping)
    {
        lock (_gate)
        {
            _looping = looping;
        }
    }

    public bool IsActive()
    {
        lock (_gate)
        {
            return _started && !_videoFinished;
        }
    }

    public ulong CurrentTimeMs()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return 0;
            }

            var ms = _clock.ElapsedMilliseconds;
            if (_durationMs > 0 && ms > (long)_durationMs)
            {
                ms = (long)_durationMs;
            }

            return (ulong)ms;
        }
    }

    /// <summary>
    /// Delivers the next video frame into <paramref name="frameInfoAddress"/> when
    /// wall-clock time has reached its presentation timestamp. Returns 1 when a new
    /// frame was written, 0 when it is not yet time for the next frame or the clip
    /// has ended (the previously written frame stays valid, matching shadPS4).
    /// </summary>
    public int GetVideoData(CpuContext ctx, ulong frameInfoAddress, bool extended)
    {
        lock (_gate)
        {
            if (!_started || _videoFinished || !_hasVideo || _videoStdout is null || frameInfoAddress == 0)
            {
                return 0;
            }

            var now = _clock.ElapsedMilliseconds;
            var nextPtsMs = _videoFrameIndex * 1000.0 / _fps;
            if (now < nextPtsMs)
            {
                return 0;
            }

            _videoHost ??= new byte[_frameSize];
            var read = ReadFully(_videoStdout, _videoHost, _frameSize);
            if (read < _frameSize)
            {
                if (_looping && read == 0 && RestartStreamsLocked())
                {
                    now = 0;
                    nextPtsMs = 0;
                    read = ReadFully(_videoStdout!, _videoHost, _frameSize);
                }

                if (read < _frameSize)
                {
                    _videoFinished = true;
                    Trace($"video_eof handle=0x{_handle:X} frames={_videoFrameIndex}");
                    return 0;
                }
            }

            if (!EnsureGuestBuffer(ctx, _frameSize, ref _guestVideo, ref _guestVideoCapacity) ||
                !ctx.Memory.TryWrite(_guestVideo, _videoHost.AsSpan(0, _frameSize)))
            {
                _videoFinished = true;
                return 0;
            }

            var timestampMs = (ulong)nextPtsMs;
            if (!WriteVideoFrameInfo(ctx, frameInfoAddress, extended, timestampMs))
            {
                return 0;
            }

            _lastTimestampMs = (long)timestampMs;
            _videoFrameIndex++;
            return 1;
        }
    }

    /// <summary>
    /// Delivers the next PCM audio chunk whenever ffmpeg has one available; the
    /// guest paces consumption through its own audio callback. Returns 1 when a
    /// chunk was written, 0 at end of stream.
    /// </summary>
    public int GetAudioData(CpuContext ctx, ulong frameInfoAddress)
    {
        lock (_gate)
        {
            if (!_started || !_hasAudio || _audioStdout is null || frameInfoAddress == 0)
            {
                return 0;
            }

            var read = ReadFully(_audioStdout, _audioHost, AudioChunkBytes);
            if (read <= 0)
            {
                return 0;
            }

            var capacity = AudioChunkBytes;
            if (!EnsureGuestBuffer(ctx, AudioChunkBytes, ref _guestAudio, ref capacity) ||
                !ctx.Memory.TryWrite(_guestAudio, _audioHost.AsSpan(0, read)))
            {
                return 0;
            }

            var samples = read / (AudioChannels * AudioBytesPerSample);
            return WriteAudioFrameInfo(ctx, frameInfoAddress, (ulong)_lastTimestampMs, samples, read) ? 1 : 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopStreamsLocked();
        }
    }

    // ---- ffmpeg process management -------------------------------------------------

    private bool SpawnStreamsLocked()
    {
        if (_hostPath is null)
        {
            return false;
        }

        if (_hasVideo)
        {
            _videoProcess = StartFfmpeg(
                "-nostdin", "-v", "error", "-i", _hostPath,
                "-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1");
            if (_videoProcess is null)
            {
                return false;
            }

            _videoStdout = _videoProcess.StandardOutput.BaseStream;
        }

        if (_hasAudio)
        {
            _audioProcess = StartFfmpeg(
                "-nostdin", "-v", "error", "-i", _hostPath,
                "-vn", "-f", "s16le", "-ac", AudioChannels.ToString(CultureInfo.InvariantCulture),
                "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture), "pipe:1");
            if (_audioProcess is not null)
            {
                _audioStdout = _audioProcess.StandardOutput.BaseStream;
            }
        }

        return _videoProcess is not null || _audioProcess is not null;
    }

    private bool RestartStreamsLocked()
    {
        StopStreamsLocked();
        if (!SpawnStreamsLocked())
        {
            return false;
        }

        _videoFrameIndex = 0;
        _lastTimestampMs = 0;
        _clock.Restart();
        Trace($"loop_restart handle=0x{_handle:X}");
        return true;
    }

    private void StopStreamsLocked()
    {
        KillProcess(ref _videoProcess);
        KillProcess(ref _audioProcess);
        _videoStdout = null;
        _audioStdout = null;
    }

    private static void KillProcess(ref Process? process)
    {
        var target = process;
        process = null;
        if (target is null)
        {
            return;
        }

        try
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The child may have already exited; nothing else to reclaim.
        }
        finally
        {
            target.Dispose();
        }
    }

    private static Process? StartFfmpeg(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            // Drain stderr on a background task so a chatty decoder can never wedge
            // the child by filling its (unread) error pipe.
            var stderr = process.StandardError;
            _ = Task.Run(() =>
            {
                try
                {
                    var text = stderr.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Trace($"ffmpeg_stderr {text.Trim()}");
                    }
                }
                catch (Exception)
                {
                    // Ignore teardown races when the process is killed mid-read.
                }
            });

            return process;
        }
        catch (Exception ex)
        {
            Trace($"ffmpeg_start_failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            int read;
            try
            {
                read = stream.Read(buffer, total, count - total);
            }
            catch (Exception)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    // ---- probing -------------------------------------------------------------------

    private bool Probe(string file)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = FfprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-v", "error", "-print_format", "json",
                         "-show_streams", "-show_format", file,
                     })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var json = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            _hasVideo = false;
            _hasAudio = false;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType))
                    {
                        continue;
                    }

                    var type = codecType.GetString();
                    if (string.Equals(type, "video", StringComparison.Ordinal) && !_hasVideo)
                    {
                        _width = ReadInt(stream, "width");
                        _height = ReadInt(stream, "height");
                        _fps = ReadRational(stream, "r_frame_rate", 30.0);
                        _hasVideo = _width > 0 && _height > 0 && _width <= 8192 && _height <= 8192;
                    }
                    else if (string.Equals(type, "audio", StringComparison.Ordinal))
                    {
                        _hasAudio = true;
                    }
                }
            }

            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var duration) &&
                double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                _durationMs = seconds * 1000.0;
            }

            if (_hasVideo)
            {
                var chromaWidth = (_width + 1) / 2;
                var chromaHeight = (_height + 1) / 2;
                _frameSize = (_width * _height) + (2 * chromaWidth * chromaHeight);
            }

            return _hasVideo || _hasAudio;
        }
        catch (Exception ex)
        {
            Trace($"probe_failed {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static double ReadRational(JsonElement element, string name, double fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        var text = value.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) && single > 0
                ? single
                : fallback;
        }

        if (double.TryParse(text[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(text[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            numerator > 0 && denominator > 0)
        {
            return numerator / denominator;
        }

        return fallback;
    }

    // ---- guest frame-info writers --------------------------------------------------

    private bool WriteVideoFrameInfo(CpuContext ctx, ulong address, bool extended, ulong timestampMs)
    {
        // Common SceAvPlayerFrameInfo(Ex) header:
        //   0x00 p_data (u64)   0x08 reserved[4]   0x0C pad   0x10 timestamp (u64)
        //   0x18 details (union: 16 bytes for FrameInfo, 80 bytes for FrameInfoEx)
        var size = extended ? 104 : 40;
        Span<byte> buffer = stackalloc byte[104];
        buffer.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, _guestVideo);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x10..], timestampMs);

        var details = buffer[0x18..];
        if (extended)
        {
            // SceAvPlayerVideoEx
            BinaryPrimitives.WriteUInt32LittleEndian(details, (uint)_width);            // 0x00 width
            BinaryPrimitives.WriteUInt32LittleEndian(details[0x04..], (uint)_height);   // 0x04 height
            BinaryPrimitives.WriteSingleLittleEndian(details[0x08..], 1.0f);            // 0x08 aspect_ratio
            BinaryPrimitives.WriteUInt32LittleEndian(details[0x10..], (uint)Math.Round(_fps)); // 0x10 framerate
            BinaryPrimitives.WriteUInt32LittleEndian(details[0x24..], (uint)_width);    // 0x24 pitch
            details[0x28] = 8;  // luma_bit_depth
            details[0x29] = 8;  // chroma_bit_depth
        }
        else
        {
            // SceAvPlayerVideo
            BinaryPrimitives.WriteUInt32LittleEndian(details, (uint)_width);            // 0x00 width
            BinaryPrimitives.WriteUInt32LittleEndian(details[0x04..], (uint)_height);   // 0x04 height
            BinaryPrimitives.WriteSingleLittleEndian(details[0x08..], 1.0f);            // 0x08 aspect_ratio
        }

        return ctx.Memory.TryWrite(address, buffer[..size]);
    }

    private bool WriteAudioFrameInfo(CpuContext ctx, ulong address, ulong timestampMs, int samples, int sizeBytes)
    {
        // SceAvPlayerFrameInfo with SceAvPlayerAudio details:
        //   0x18 channel_count (u16)   0x1C sample_rate (u32)   0x20 size (u32)
        Span<byte> buffer = stackalloc byte[40];
        buffer.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, _guestAudio);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x10..], timestampMs);

        var details = buffer[0x18..];
        BinaryPrimitives.WriteUInt16LittleEndian(details, (ushort)AudioChannels);       // 0x00 channel_count
        BinaryPrimitives.WriteUInt32LittleEndian(details[0x04..], AudioSampleRate);     // 0x04 sample_rate
        BinaryPrimitives.WriteUInt32LittleEndian(details[0x08..], (uint)sizeBytes);     // 0x08 size
        _ = samples;

        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool EnsureGuestBuffer(CpuContext ctx, int length, ref ulong address, ref int capacity)
    {
        if (address != 0 && capacity >= length)
        {
            return true;
        }

        if (!KernelMemoryCompatExports.TryAllocateHleGuestBuffer(ctx, (ulong)length, out var allocated))
        {
            return false;
        }

        address = allocated;
        capacity = length;
        return true;
    }

    // ---- path resolution -----------------------------------------------------------

    private static string? ResolveHostPath(string guestPath)
    {
        if (string.IsNullOrWhiteSpace(guestPath))
        {
            return null;
        }

        var normalized = guestPath.Replace('\\', '/').Trim();
        var app0Root = ResolveApp0Root();

        if (app0Root is not null)
        {
            foreach (var prefix in new[] { "/app0/", "app0/" })
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = normalized[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                    return Path.Combine(app0Root, relative);
                }
            }
        }

        // Already a real host path (e.g. a test harness passing an absolute file).
        if (File.Exists(normalized))
        {
            return normalized;
        }

        // Relative guest path with no explicit mount: interpret against app0.
        if (app0Root is not null && !normalized.StartsWith('/'))
        {
            return Path.Combine(app0Root, normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return normalized;
    }

    private static string? ResolveApp0Root()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    // ---- ffmpeg discovery ----------------------------------------------------------

    private static readonly string FfmpegPath = ResolveTool("SHARPEMU_FFMPEG_PATH", "ffmpeg");
    private static readonly string FfprobePath = ResolveTool("SHARPEMU_FFPROBE_PATH", "ffprobe");

    private static string ResolveTool(string overrideEnv, string toolName)
    {
        var configured = Environment.GetEnvironmentVariable(overrideEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var homebrew = Path.Combine("/opt/homebrew/bin", toolName);
        return File.Exists(homebrew) ? homebrew : toolName;
    }

    // ---- tracing -------------------------------------------------------------------

    internal static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AVPLAYER"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] avplayer.{message}");
        }
    }
}
