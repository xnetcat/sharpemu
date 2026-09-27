// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// LOCAL ONLY diagnostic: settings an A/B run can change while the game runs. The file named by
/// SHARPEMU_LIVE_TUNE_FILE holds key=value lines and is re-read at most once a second, so one launch
/// measures both sides of a change instead of paying for a second launch.
/// </summary>
internal static class LiveTune
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SHARPEMU_LIVE_TUNE_FILE");
    private static long _nextRead;
    private static Dictionary<string, string> _values = new();

    public static bool Enabled => Path is not null;

    public static string? Get(string key)
    {
        if (Path is null)
        {
            return null;
        }

        var now = Stopwatch.GetTimestamp();
        if (now >= Volatile.Read(ref _nextRead))
        {
            Volatile.Write(ref _nextRead, now + Stopwatch.Frequency);
            var values = new Dictionary<string, string>();
            try
            {
                foreach (var line in File.ReadAllLines(Path))
                {
                    var separator = line.IndexOf('=');
                    if (separator > 0)
                    {
                        values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }
            }
            catch (IOException)
            {
            }

            if (!DictionaryEquals(values, _values))
            {
                Console.Error.WriteLine($"[PERF][LIVE_TUNE] {string.Join(' ', values.Select(static pair => pair.Key + "=" + pair.Value))}");
            }

            _values = values;
        }

        return _values.GetValueOrDefault(key);
    }

    private static bool DictionaryEquals(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
