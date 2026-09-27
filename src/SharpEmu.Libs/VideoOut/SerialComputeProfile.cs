// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.VideoOut;

// The executor records around each dispatch; the presenter publishes its scheduler at device setup.

// LOCAL DIAGNOSTIC (SHARPEMU_PROFILE_SERIAL_COMPUTE=1): drains the queue before and after every
// compute dispatch and charges the second drain to the dispatch's pipeline. GPU timestamps are not
// trustworthy on MoltenVK, so this measures each dispatch's GPU time with the host clock. The first
// drain's time is charged to "between" (draws and copies recorded since the previous dispatch).
internal static class SerialComputeProfile
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SERIAL_COMPUTE") == "1";

    private static readonly Dictionary<ulong, (double Ms, long Count, double MaxMs, string Args)> Pipelines = new();
    private static double _betweenMs;
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static SubmissionScheduler? Scheduler;

    public static void Before()
    {
        var start = Stopwatch.GetTimestamp();
        Scheduler?.Finish();
        _betweenMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public static void After(ulong pipeline, uint x, uint y, uint z)
    {
        var start = Stopwatch.GetTimestamp();
        Scheduler?.Finish();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Pipelines.TryGetValue(pipeline, out var entry);
        Pipelines[pipeline] = (entry.Ms + ms, entry.Count + 1, Math.Max(entry.MaxMs, ms), ms >= entry.MaxMs ? $"{x},{y},{z}" : entry.Args);
        if (Stopwatch.GetElapsedTime(_windowStart).TotalSeconds >= 10)
        {
            Report();
        }
    }

    private static void Report()
    {
        var window = Stopwatch.GetElapsedTime(_windowStart).TotalMilliseconds;
        var total = Pipelines.Values.Sum(static entry => entry.Ms);
        Console.Error.WriteLine($"[PERF][SERIAL_COMPUTE] window_ms={window:F0} dispatch_ms={total:F0} between_ms={_betweenMs:F0}");
        foreach (var (pipeline, entry) in Pipelines.OrderByDescending(static pair => pair.Value.Ms).Take(12))
        {
            Console.Error.WriteLine(
                $"[PERF][SERIAL_COMPUTE] cs=0x{pipeline:X16} total_ms={entry.Ms:F1} n={entry.Count} max_ms={entry.MaxMs:F2} max_args={entry.Args}");
        }

        Pipelines.Clear();
        _betweenMs = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }
}
