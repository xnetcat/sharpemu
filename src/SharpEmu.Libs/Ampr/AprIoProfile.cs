// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Ampr;

// LOCAL ONLY: wall-time attribution for APR startup and streaming. Blocking and
// scheduling delays are included; these counters must not be labelled CPU time.
internal static class AprIoProfile
{
    internal enum Phase { PathText, FileSize, RegisterPath, HostOpen, HostRead, GuestWrite, Count }
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_APR_IO") == "1";
    private static readonly long[] Ticks = new long[(int)Phase.Count];
    private static readonly long[] Calls = new long[(int)Phase.Count];
    private static long _nextReport;

    internal static Scope Measure(Phase phase) => Enabled ? new(phase) : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly Phase _phase;
        private readonly long _started;
        internal Scope(Phase phase) { _phase = phase; _started = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_started == 0) return;
            var now = Stopwatch.GetTimestamp();
            Interlocked.Add(ref Ticks[(int)_phase], now - _started);
            Interlocked.Increment(ref Calls[(int)_phase]);
            var next = Volatile.Read(ref _nextReport);
            if (now < next || Interlocked.CompareExchange(ref _nextReport, now + Stopwatch.Frequency * 10, next) != next)
                return;
            for (var i = 0; i < (int)Phase.Count; i++)
                Console.Error.WriteLine($"[PERF][APR_IO] phase={(Phase)i} calls={Volatile.Read(ref Calls[i])} elapsed_total_ms={Volatile.Read(ref Ticks[i]) * 1000.0 / Stopwatch.Frequency:F1}");
        }
    }
}
