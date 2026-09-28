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
    private static readonly CallSiteProfile _profile = new("SUBMIT_SITE", "SHARPEMU_PROFILE_SUBMIT_SITES");

    public static bool Enabled => _profile.Enabled;

    public static void Record() => _profile.Record();

    public static void Report()
    {
        _profile.Report();
        RenderBreakProfile.Report();
    }
}

/// <summary>
/// LOCAL ONLY diagnostic: counts every ended dynamic-rendering scope by the managed call site
/// that ended it, so a report says what splits a guest render pass into many small ones.
/// Enabled with SHARPEMU_PROFILE_RENDER_BREAKS=1.
/// </summary>
internal static class RenderBreakProfile
{
    private static readonly CallSiteProfile _profile = new("RENDER_BREAK", "SHARPEMU_PROFILE_RENDER_BREAKS");

    public static bool Enabled => _profile.Enabled;

    public static void Record() => _profile.Record();

    public static void Report() => _profile.Report();
}

internal sealed class CallSiteProfile(string tag, string environmentVariable)
{
    public readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable(environmentVariable), "1", StringComparison.Ordinal);

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new();
    private long _windowStart = Stopwatch.GetTimestamp();

    public void Record()
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
        var trace = new StackTrace(3, false);
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

    public void Report()
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

        Console.Error.WriteLine($"[PERF][{tag}] window_s={seconds:F1} submits={ranked.Sum(static pair => pair.Value)}");
        foreach (var (key, count) in ranked.Take(16))
        {
            Console.Error.WriteLine($"[PERF][{tag}] n={count} at={key}");
        }
    }
}
