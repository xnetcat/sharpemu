// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Diagnostics;

// LOCAL ONLY: sample actual CPU and the fiber gate separately from the time
// spent validating, reading and updating guest state. Never changes scheduling.
internal static class FiberSwitchProfile
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_FIBER_SWITCH") == "1";
    [ThreadStatic] private static int _calls;
    private readonly record struct Sample(long Timestamp, int Thread, ulong Guest,
        long Wall, long Cpu, long GateWait, long GateWaitCpu, long GateHeld);
    private static readonly object Gate = new();
    private static readonly Sample[] Samples = new Sample[8192];
    private static long _count;

    internal static Scope? Begin() => Enabled && (++_calls & 15) == 0 ? new Scope() : null;

    internal sealed class Scope : IDisposable
    {
        private readonly long _started, _cpu;
        private long _waiting, _waitingCpu, _entered, _gateWait, _gateWaitCpu, _gateHeld;

        internal Scope()
        {
            _cpu = GuestProducerProfile.ReadCurrentCpuNanoseconds();
            _started = Stopwatch.GetTimestamp();
        }

        internal void GateWaiting()
        {
            _waitingCpu = GuestProducerProfile.ReadCurrentCpuNanoseconds();
            _waiting = Stopwatch.GetTimestamp();
        }

        internal void GateEntered()
        {
            _entered = Stopwatch.GetTimestamp();
            _gateWait = _entered - _waiting;
            var cpu = GuestProducerProfile.ReadCurrentCpuNanoseconds();
            _gateWaitCpu = cpu >= _waitingCpu && _waitingCpu >= 0 ? cpu - _waitingCpu : -1;
        }

        internal void GateExited()
        {
            if (_entered != 0)
            {
                _gateHeld = Stopwatch.GetTimestamp() - _entered;
                _entered = 0;
            }
        }

        public void Dispose()
        {
            var now = Stopwatch.GetTimestamp();
            var cpu = GuestProducerProfile.ReadCurrentCpuNanoseconds();
            var sample = new Sample(now, Environment.CurrentManagedThreadId,
                GuestThreadExecution.CurrentGuestThreadHandle, now - _started,
                cpu >= _cpu && _cpu >= 0 ? cpu - _cpu : -1, _gateWait, _gateWaitCpu, _gateHeld);
            lock (Gate) Samples[_count++ % Samples.Length] = sample;
        }
    }

    internal static void WriteTrace()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            var first = Math.Max(0, _count - Samples.Length);
            Console.Error.WriteLine($"[PERF][FIBER_SWITCH_TRACE] retained={_count - first} overwritten={first} sample_every=16");
            for (var index = first; index < _count; index++)
            {
                var sample = Samples[index % Samples.Length];
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[PERF][FIBER_SWITCH] timestamp={sample.Timestamp} thread={sample.Thread} guest=0x{sample.Guest:X} wall_ms={sample.Wall * 1000.0 / Stopwatch.Frequency:F3} cpu_ms={sample.Cpu / 1_000_000.0:F3} gate_wait_ms={sample.GateWait * 1000.0 / Stopwatch.Frequency:F3} gate_wait_cpu_ms={sample.GateWaitCpu / 1_000_000.0:F3} gate_held_ms={sample.GateHeld * 1000.0 / Stopwatch.Frequency:F3}"));
            }
            _count = 0;
            Array.Clear(Samples);
        }
    }
}
