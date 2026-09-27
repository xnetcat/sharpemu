// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// SHARPEMU_PROFILE_MAPPED_DOWNLOADS=1: how far behind the open tick mapped downloads wait, and how long.
internal static class MappedDownloadProfile
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_MAPPED_DOWNLOADS") == "1";
    private static long _count, _olderTick, _openTick, _ticks;
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static Scope Measure(ulong tick, ulong currentTick) => new(Enabled, tick, currentTick);

    public readonly struct Scope : IDisposable
    {
        private readonly bool _enabled;
        private readonly long _start;
        private readonly bool _current;

        public Scope(bool enabled, ulong tick, ulong currentTick)
        {
            _enabled = enabled;
            _start = enabled ? Stopwatch.GetTimestamp() : 0;
            _current = tick >= currentTick;
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            _ticks += Stopwatch.GetTimestamp() - _start;
            _count++;
            if (_current) _openTick++; else _olderTick++;
            if (Stopwatch.GetElapsedTime(_windowStart).TotalSeconds >= 10)
            {
                Console.Error.WriteLine(
                    $"[PERF][MAPPED_DOWNLOAD] n={_count} open_tick={_openTick} older_tick={_olderTick} wait_ms={_ticks * 1000.0 / Stopwatch.Frequency:F1}");
                _count = _olderTick = _openTick = _ticks = 0;
                _windowStart = Stopwatch.GetTimestamp();
            }
        }
    }
}
