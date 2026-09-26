// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Diagnostic (SHARPEMU_TRACE_GPU_WORK=1): the draws and dispatches recorded into recent
// scheduler ticks, so a tick that never completes can be traced to the work it carried.
public static class GpuWorkTrace
{
    private const int Capacity = 4096;

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GPU_WORK") == "1";

    // Diagnostic (SHARPEMU_WATCH_GPU_ADDRESS=0x...,0x...): every listed address is read back
    // after each dispatch that writes one of them.
    public static readonly ulong[] WatchedAddresses =
        (Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GPU_ADDRESS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static text => ulong.Parse(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();

    public static ulong? WatchedAddress => WatchedAddresses.Length == 0 ? null : WatchedAddresses[0];

    private static readonly (ulong Tick, string Work)[] Entries = new (ulong, string)[Capacity];
    private static readonly object Gate = new();
    private static int _next;

    // The tick the host records into now.
    public static Func<ulong>? CurrentTick { get; set; }

    public static void Note(string work)
    {
        if (!Enabled || CurrentTick is not { } tick)
        {
            return;
        }

        lock (Gate)
        {
            Entries[_next] = (tick(), work);
            _next = (_next + 1) % Capacity;
        }
    }

    // The work of the given tick and the ticks just before it.
    public static void Report(ulong tick, int previousTicks = 6)
    {
        for (var earlier = (ulong)Math.Min((ulong)previousTicks, tick); earlier > 0; earlier--)
        {
            ReportTick(tick - earlier);
        }

        ReportTick(tick);
    }

    private static void ReportTick(ulong tick)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            var count = 0;
            for (var offset = 0; offset < Capacity; offset++)
            {
                var (entryTick, work) = Entries[(_next + offset) % Capacity];
                if (work is not null && entryTick == tick)
                {
                    Console.Error.WriteLine($"[GPU][ERROR]   tick {tick}: {work}");
                    count++;
                }
            }

            Console.Error.WriteLine($"[GPU][ERROR]   tick {tick} carried {count} traced work items");
        }
    }
}
