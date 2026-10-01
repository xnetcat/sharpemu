// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

// Cumulative counters sampled at command submission. Deltas on the same host
// thread separate actual CPU consumption from elapsed producer gaps. HLE fields
// are wall time in HLE (including guest callbacks), not CPU or inferred off-CPU
// time. Nested imports partition that time, and snapshots include open scopes.
public static partial class GuestProducerProfile
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GUEST_PRODUCER") == "1";
    [ThreadStatic] private static Counters? _counters;
    internal sealed class Counters
    {
        internal readonly long[] HleTicks = new long[8];
        internal int ActiveCategory = -1;
        internal long AccountedThrough;
        internal void Advance(long now)
        {
            if (ActiveCategory >= 0) HleTicks[ActiveCategory] += now - AccountedThrough;
            AccountedThrough = now;
        }
        internal long NextNativeSample, NativeStarted, NativeCpuStarted;
        internal ulong NativeReturn;
        internal string NativeImport = "none";
        internal long LongestTicks;
        internal string LongestImport = "none";
    }

    public readonly record struct Sample(long CpuNs, long MutexTicks, long ConditionTicks, long SemaphoreTicks,
        long AprTicks, long EventTicks, long SleepTicks, long GpuTicks, long OtherTicks, string LongestImport, long LongestTicks);

    public static Sample Capture()
    {
        if (!Enabled) return default;
        var counters = _counters ??= new();
        counters.Advance(Stopwatch.GetTimestamp());
        var ticks = counters.HleTicks;
        var sample = new Sample(ReadCurrentCpuNanoseconds(), ticks[0], ticks[1], ticks[2], ticks[3], ticks[4], ticks[5], ticks[6], ticks[7],
            counters.LongestImport, counters.LongestTicks);
        counters.LongestTicks = 0;
        counters.LongestImport = "none";
        return sample;
    }

    public static int ClassifyImport(string name) =>
        name.Contains("Mutex", StringComparison.OrdinalIgnoreCase) ? 0 :
        name.Contains("Cond", StringComparison.OrdinalIgnoreCase) ? 1 :
        name.Contains("Sema", StringComparison.OrdinalIgnoreCase) ? 2 :
        name.Contains("Apr", StringComparison.OrdinalIgnoreCase) || name.Contains("Ampr", StringComparison.OrdinalIgnoreCase) ? 3 :
        name.Contains("Equeue", StringComparison.OrdinalIgnoreCase) || name.Contains("EventFlag", StringComparison.OrdinalIgnoreCase) ? 4 :
        name.Contains("Sleep", StringComparison.OrdinalIgnoreCase) ? 5 :
        name.Contains("Agc", StringComparison.OrdinalIgnoreCase) || name.Contains("VideoOut", StringComparison.OrdinalIgnoreCase) ? 6 : 7;

    public static Scope MeasureImport(string name, int category, ulong returnAddress)
    {
        if (!Enabled) return default;
        var counters = _counters ??= new();
        var now = Stopwatch.GetTimestamp();
        CompleteNativeSample(counters, now, name, returnAddress);
        return new(counters, category, now, name, returnAddress);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Counters? _counters;
        private readonly int _category;
        private readonly long _started;
        private readonly string? _name;
        private readonly int _previousCategory;
        private readonly ulong _returnAddress;
        internal Scope(Counters counters, int category, long started, string name, ulong returnAddress)
        {
            (_counters, _category, _started, _name, _returnAddress) = (counters, category, started, name, returnAddress);
            counters.Advance(started);
            _previousCategory = counters.ActiveCategory;
            counters.ActiveCategory = category;
        }
        public void Dispose()
        {
            if (_counters is null) return;
            if (_category >= 0)
            {
                var now = Stopwatch.GetTimestamp();
                _counters.Advance(now);
                var elapsed = now - _started;
                if (elapsed > _counters.LongestTicks)
                {
                    _counters.LongestTicks = elapsed;
                    _counters.LongestImport = _name!;
                }
            }
            _counters.ActiveCategory = _previousCategory;
            StartNativeSample(_counters, _name!, _returnAddress);
        }
    }

    public static long ReadCurrentCpuNanoseconds()
    {
        if (!OperatingSystem.IsMacOS()) return -1;
        // Mach reports microseconds correctly under Rosetta. Its clock_gettime
        // thread clock has returned raw counter ticks on supported host builds.
        uint count = 10;
        return ThreadInfo(PthreadMachThread(PthreadSelf()), 3, out var value, ref count) == 0
            ? ((long)value.UserSeconds + value.SystemSeconds) * 1_000_000_000 +
              ((long)value.UserMicroseconds + value.SystemMicroseconds) * 1_000 : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadBasicInfo
    {
        public int UserSeconds, UserMicroseconds, SystemSeconds, SystemMicroseconds;
        public int CpuUsage, Policy, RunState, Flags, SuspendCount, SleepTime;
    }
    [DllImport("libSystem.dylib", EntryPoint = "thread_info")]
    private static extern int ThreadInfo(uint thread, int flavor, out ThreadBasicInfo value, ref uint count);
    [DllImport("libSystem.dylib", EntryPoint = "pthread_mach_thread_np")]
    private static extern uint PthreadMachThread(nint thread);
    [DllImport("libSystem.dylib", EntryPoint = "pthread_self")]
    private static extern nint PthreadSelf();
}
