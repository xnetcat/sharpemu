// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Media;

/// <summary>
/// Planar YUV 4:2:0 layout of a host movie frame: a full-resolution luma plane
/// followed by one interleaved chroma pair per 2x2 block. It is the layout the
/// guest's Bink shaders sample, so the presenter uploads the planes as-is.
/// </summary>
internal static unsafe class HostMovieYuv420
{
    internal static int LumaLength(uint width, uint height) =>
        checked((int)((ulong)width * height));

    internal static int ChromaLength(uint width, uint height) =>
        checked((int)((ulong)((width + 1) / 2) * ((height + 1) / 2) * 2));

    internal static int FrameLength(uint width, uint height) =>
        checked(LumaLength(width, height) + ChromaLength(width, height));

    /// <summary>
    /// Converts a BGRA frame into <paramref name="destination"/>: the luma
    /// plane, then the chroma plane. Each chroma pair averages its 2x2 block,
    /// clipped at odd edges.
    /// </summary>
    internal static void ConvertFromBgra(
        ReadOnlySpan<byte> bgra,
        uint width,
        uint height,
        Span<byte> destination)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        var lumaLength = LumaLength(width, height);
        if (bgra.Length < (long)lumaLength * 4 ||
            destination.Length < FrameLength(width, height))
        {
            throw new ArgumentException("Host movie frame buffers are too small.");
        }

        fixed (byte* source = bgra, luma = destination)
        {
            ConvertRowPairs(source, (int)width, (int)height, luma, luma + lumaLength);
        }
    }

    private static void ConvertRowPairs(
        byte* bgra,
        int width,
        int height,
        byte* luma,
        byte* chroma)
    {
        var chromaWidth = (width + 1) / 2;
        var rowPairs = (height + 1) / 2;
        for (var pair = 0; pair < rowPairs; pair++)
        {
            var y = pair * 2;
            var hasSecondRow = y + 1 < height;
            var row0 = bgra + (nint)y * width * 4;
            var row1 = row0 + (nint)width * 4;
            var luma0 = luma + (nint)y * width;
            var luma1 = luma0 + width;
            var chromaRow = chroma + (nint)pair * chromaWidth * 2;
            for (var x = 0; x < width; x += 2)
            {
                var hasSecondColumn = x + 1 < width;
                var pixel = row0 + x * 4;
                int blue = pixel[0], green = pixel[1], red = pixel[2];
                luma0[x] = LumaFromRgb(red, green, blue);
                var samples = 1;
                if (hasSecondColumn)
                {
                    AccumulatePixel(pixel + 4, luma0 + x + 1, ref red, ref green, ref blue);
                    samples++;
                }

                if (hasSecondRow)
                {
                    pixel = row1 + x * 4;
                    AccumulatePixel(pixel, luma1 + x, ref red, ref green, ref blue);
                    samples++;
                    if (hasSecondColumn)
                    {
                        AccumulatePixel(pixel + 4, luma1 + x + 1, ref red, ref green, ref blue);
                        samples++;
                    }
                }

                red /= samples;
                green /= samples;
                blue /= samples;
                var destination = chromaRow + x;
                destination[0] = ClampByte(
                    ((128 * red - 116 * green - 12 * blue + 128) >> 8) + 128);
                destination[1] = ClampByte(
                    ((-29 * red - 99 * green + 128 * blue + 128) >> 8) + 128);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulatePixel(
        byte* pixel,
        byte* lumaDestination,
        ref int red,
        ref int green,
        ref int blue)
    {
        int b = pixel[0], g = pixel[1], r = pixel[2];
        *lumaDestination = LumaFromRgb(r, g, b);
        red += r;
        green += g;
        blue += b;
    }

    // The weights sum to 256, so the result never exceeds 255.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte LumaFromRgb(int red, int green, int blue) =>
        (byte)((54 * red + 183 * green + 19 * blue + 128) >> 8);

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}

/// <summary>
/// Converts each decoded BGRA frame to <see cref="HostMovieYuv420"/> planes on
/// the playback's decoder thread, so the render thread only copies them.
/// </summary>
internal sealed class HostMovieYuv420Decoder : IMediaFrameDecoder
{
    private static readonly bool ProfileDecode =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_MOVIE_DECODE") == "1";
    private readonly IMediaFrameDecoder _inner;
    private readonly byte[] _bgra;
    private long _decodeTicks;
    private long _convertTicks;
    private long _decodeCpuNanoseconds;
    private long _convertCpuNanoseconds;
    private int _decodedFrames;
    private static readonly int RaiseQosAfterFrames =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MOVIE_QOS_AFTER_FRAMES"), out var frames) ? frames : -1;

    internal HostMovieYuv420Decoder(IMediaFrameDecoder inner)
    {
        _inner = inner;
        _bgra = GC.AllocateUninitializedArray<byte>(
            checked((int)((ulong)inner.Width * inner.Height * 4)));
    }

    public uint Width => _inner.Width;

    public uint Height => _inner.Height;

    public uint FramesPerSecondNumerator => _inner.FramesPerSecondNumerator;

    public uint FramesPerSecondDenominator => _inner.FramesPerSecondDenominator;

    public bool TryDecodeNextFrame(Span<byte> destination)
    {
        if (OperatingSystem.IsMacOS() && ProfileDecode && _decodedFrames == 0)
        {
            var result = GetQos(PthreadSelf(), out var qos, out var relative);
            GetScheduling(PthreadSelf(), out var policy, out var scheduling);
            Console.Error.WriteLine($"[PERF][MOVIE_QOS] initial=0x{qos:X} relative={relative} result={result} policy={policy} priority={scheduling.Priority}");
        }
        if (OperatingSystem.IsMacOS() && ProfileDecode && _decodedFrames == RaiseQosAfterFrames)
            Console.Error.WriteLine($"[PERF][MOVIE_QOS] user_initiated result={SetQos(0x19, 0)} frames={_decodedFrames}");
        if (ProfileDecode && _decodedFrames == 90 &&
            Environment.GetEnvironmentVariable("SHARPEMU_MOVIE_MANAGED_PRIORITY") == "1")
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            GetScheduling(PthreadSelf(), out var policy, out var scheduling);
            Console.Error.WriteLine($"[PERF][MOVIE_QOS] managed priority={Thread.CurrentThread.Priority} frames={_decodedFrames} policy={policy} native_priority={scheduling.Priority}");
        }
        var started = ProfileDecode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var cpuStarted = ProfileDecode ? CpuNanoseconds() : 0;
        if (!_inner.TryDecodeNextFrame(_bgra))
        {
            return false;
        }

        var converted = ProfileDecode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var cpuConverted = ProfileDecode ? CpuNanoseconds() : 0;
        HostMovieYuv420.ConvertFromBgra(_bgra, Width, Height, destination);
        if (ProfileDecode)
        {
            _decodeTicks += converted - started;
            _convertTicks += System.Diagnostics.Stopwatch.GetTimestamp() - converted;
            _decodeCpuNanoseconds += cpuConverted - cpuStarted;
            _convertCpuNanoseconds += CpuNanoseconds() - cpuConverted;
            if (++_decodedFrames % 30 == 0)
            {
                var scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine($"[PERF][MOVIE_DECODE] frames={_decodedFrames} decode_ms={_decodeTicks * scale:F1} convert_ms={_convertTicks * scale:F1} decode_cpu_ms={_decodeCpuNanoseconds / 1_000_000.0:F1} convert_cpu_ms={_convertCpuNanoseconds / 1_000_000.0:F1}");
            }
        }
        return true;
    }

    public void Dispose() => _inner.Dispose();

    private static long CpuNanoseconds()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        // clock_gettime(CLOCK_THREAD_CPUTIME_ID) under this Rosetta build
        // reports raw 24 MHz ticks as nanoseconds; thread_info returns valid
        // microseconds in the same one-second CPU calibration probe.
        uint count = 10;
        return ThreadInfo(PthreadMachThread(PthreadSelf()), 3, out var value, ref count) == 0
            ? ((long)value.UserSeconds + value.SystemSeconds) * 1_000_000_000 +
              ((long)value.UserMicroseconds + value.SystemMicroseconds) * 1_000 : 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ThreadBasicInfo
    {
        public int UserSeconds, UserMicroseconds, SystemSeconds, SystemMicroseconds;
        public int CpuUsage, Policy, RunState, Flags, SuspendCount, SleepTime;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 64)]
    private struct SchedulingParameters { public int Priority; }
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "pthread_getschedparam")]
    private static extern int GetScheduling(nint thread, out int policy, out SchedulingParameters parameters);
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "thread_info")]
    private static extern int ThreadInfo(uint thread, int flavor, out ThreadBasicInfo value, ref uint count);
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "pthread_mach_thread_np")]
    private static extern uint PthreadMachThread(nint thread);
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "pthread_self")]
    private static extern nint PthreadSelf();
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "pthread_get_qos_class_np")]
    private static extern int GetQos(nint thread, out uint qos, out int relative);
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQos(uint qos, int relative);
}
