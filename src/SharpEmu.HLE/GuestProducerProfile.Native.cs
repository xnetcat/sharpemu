// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE;

public static partial class GuestProducerProfile
{
    // Cooperative sampling never suspends Rosetta or asks it to unwind a guest
    // stack. A sample spans one chosen HLE return to the next HLE entry. CPU time
    // includes the native ABI gateways and intervening guest code. These are
    // selected intervals, not a statistical instruction profile or CPU share.
    private static readonly bool NativeEnabled = Enabled &&
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GUEST_NATIVE_GAPS") == "1";
    private readonly record struct NativeSample(long Timestamp, int Thread, ulong Guest,
        ulong From, ulong To, string FromImport, string ToImport, long WallTicks, long CpuNs);
    private static class NativeStorage
    {
        internal static readonly object Gate = new();
        internal static readonly NativeSample[] Events = new NativeSample[16384];
        internal static long Count;
        internal static bool Closed;
    }

    private static void StartNativeSample(Counters counters, string import, ulong returnAddress)
    {
        if (!NativeEnabled) return;
        var now = Stopwatch.GetTimestamp();
        if (now < counters.NextNativeSample) return;
        counters.NextNativeSample = now + Stopwatch.Frequency / 50;
        counters.NativeCpuStarted = ReadCurrentCpuNanoseconds();
        counters.NativeStarted = Stopwatch.GetTimestamp();
        counters.NativeReturn = returnAddress;
        counters.NativeImport = import;
    }

    private static void CompleteNativeSample(Counters counters, long now, string import, ulong returnAddress)
    {
        if (!NativeEnabled || counters.NativeStarted == 0) return;
        var cpu = ReadCurrentCpuNanoseconds();
        var start = counters.NativeStarted;
        counters.NativeStarted = 0;
        if (counters.NativeCpuStarted < 0 || cpu < counters.NativeCpuStarted) return;
        cpu -= counters.NativeCpuStarted;
        // Retain CPU-consuming intervals; short calls and sleeping intervals
        // have separate cumulative counters and should not crowd this ring.
        if (cpu < 50_000) return;
        var sample = new NativeSample(now, Environment.CurrentManagedThreadId,
            GuestThreadExecution.CurrentGuestThreadHandle, counters.NativeReturn, returnAddress,
            counters.NativeImport, import, now - start, cpu);
        lock (NativeStorage.Gate)
        {
            if (NativeStorage.Closed) return;
            NativeStorage.Events[NativeStorage.Count++ % NativeStorage.Events.Length] = sample;
        }
    }

    public static void WriteNativeTrace()
    {
        if (!NativeEnabled) return;
        lock (NativeStorage.Gate)
        {
            NativeStorage.Closed = true;
            var first = Math.Max(0, NativeStorage.Count - NativeStorage.Events.Length);
            Console.Error.WriteLine($"[PERF][NATIVE_GAP_TRACE] retained={NativeStorage.Count - first} overwritten={first}");
            for (var i = first; i < NativeStorage.Count; i++)
            {
                var sample = NativeStorage.Events[i % NativeStorage.Events.Length];
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[PERF][NATIVE_GAP] timestamp={sample.Timestamp} thread={sample.Thread} guest=0x{sample.Guest:X} from=0x{sample.From:X} to=0x{sample.To:X} from_import={sample.FromImport} to_import={sample.ToImport} wall_ms={sample.WallTicks * 1000.0 / Stopwatch.Frequency:F3} cpu_ms={sample.CpuNs / 1_000_000.0:F3}"));
            }
        }
    }
}
