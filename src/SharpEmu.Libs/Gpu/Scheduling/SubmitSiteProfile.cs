// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;

namespace SharpEmu.Libs.Gpu.Scheduling;

/// <summary>
/// LOCAL ONLY diagnostic: counts every guest batch the presenter submits by its managed call site,
/// so a report says which commands split a frame into many small queue submissions.
/// Enabled with SHARPEMU_PROFILE_SUBMIT_SITES=1.
/// </summary>
internal static class SubmitSiteProfile
{
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SUBMIT_SITES"), "1", StringComparison.Ordinal);

    private static readonly object _gate = new();
    private static readonly Dictionary<string, long> _counts = new();
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static void Record()
    {
        if (!Enabled)
        {
            return;
        }

        var key = Describe();
        lock (_gate)
        {
            if (!_counts.ContainsKey(key) && _counts.Count >= 128)
            {
                key = "(other)";
            }

            _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }

    private static string Describe()
    {
        var trace = new StackTrace(2, false);
        var builder = new StringBuilder();
        var kept = 0;
        for (var index = 0; index < trace.FrameCount && kept < 7; index++)
        {
            var method = trace.GetFrame(index)?.GetMethod();
            if (method?.DeclaringType is not { } type)
            {
                continue;
            }

            if (kept != 0)
            {
                builder.Append('<');
            }

            builder.Append(type.Name).Append('.').Append(method.Name);
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
        List<KeyValuePair<string, long>> ranked;
        lock (_gate)
        {
            ranked = _counts.OrderByDescending(static pair => pair.Value).ToList();
            _counts.Clear();
        }

        if (ranked.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine($"[PERF][SUBMIT_SITE] window_s={seconds:F1} submits={ranked.Sum(static pair => pair.Value)}");
        foreach (var (key, count) in ranked.Take(16))
        {
            Console.Error.WriteLine($"[PERF][SUBMIT_SITE] n={count} at={key}");
        }
    }
}
