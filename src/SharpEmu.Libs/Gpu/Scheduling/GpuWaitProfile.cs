// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;

namespace SharpEmu.Libs.Gpu.Scheduling;

/// <summary>
/// LOCAL ONLY diagnostic: attributes every timeline wait that actually blocked to
/// its managed call site, so a report says which operations force GPU fence waits.
/// Enabled with SHARPEMU_PROFILE_GPU_WAIT=1.
/// </summary>
internal static class GpuWaitProfile
{
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GPU_WAIT"), "1", StringComparison.Ordinal);

    private sealed class Entry
    {
        public long Count;
        public long Ticks;
        public long MaxTicks;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Entry> _entries = new();
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static void Record(long ticks)
    {
        if (!Enabled)
        {
            return;
        }

        var key = Describe();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= 128)
                {
                    key = "(other)";
                    if (!_entries.TryGetValue(key, out entry))
                    {
                        entry = new Entry();
                        _entries.Add(key, entry);
                    }
                }
                else
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                }
            }

            entry.Count++;
            entry.Ticks += ticks;
            if (ticks > entry.MaxTicks)
            {
                entry.MaxTicks = ticks;
            }
        }
    }

    // The first frames outside this file and its immediate scheduler plumbing.
    private static string Describe()
    {
        var trace = new StackTrace(2, false);
        var builder = new StringBuilder();
        var kept = 0;
        for (var index = 0; index < trace.FrameCount && kept < 6; index++)
        {
            var method = trace.GetFrame(index)?.GetMethod();
            if (method?.DeclaringType is not { } type)
            {
                continue;
            }

            var name = type.Name + "." + method.Name;
            if (name.StartsWith("TickTimeline.", StringComparison.Ordinal) ||
                name.StartsWith("VulkanTickDevice.", StringComparison.Ordinal) ||
                name.StartsWith("GpuWaitProfile.", StringComparison.Ordinal))
            {
                continue;
            }

            if (kept != 0)
            {
                builder.Append('<');
            }

            builder.Append(name);
            kept++;
        }

        return builder.Length == 0 ? "(unknown)" : builder.ToString();
    }

    public static void Report()
    {
        if (!Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        _windowStart = now;
        List<KeyValuePair<string, Entry>> ranked;
        lock (_gate)
        {
            ranked = _entries.OrderByDescending(static pair => pair.Value.Ticks).ToList();
            _entries.Clear();
        }

        if (ranked.Count == 0)
        {
            return;
        }

        var totalMs = ranked.Sum(static pair => pair.Value.Ticks) * 1000.0 / Stopwatch.Frequency;
        var totalCount = ranked.Sum(static pair => pair.Value.Count);
        Console.Error.WriteLine($"[PERF][GPU_WAIT] window_s={seconds:F1} blocked_waits={totalCount} blocked_ms={totalMs:F1}");
        foreach (var (key, entry) in ranked.Take(16))
        {
            Console.Error.WriteLine(
                $"[PERF][GPU_WAIT] ms={entry.Ticks * 1000.0 / Stopwatch.Frequency:F1} n={entry.Count} " +
                $"avg_ms={entry.Ticks * 1000.0 / Stopwatch.Frequency / entry.Count:F2} " +
                $"max_ms={entry.MaxTicks * 1000.0 / Stopwatch.Frequency:F2} at={key}");
        }
    }
}
