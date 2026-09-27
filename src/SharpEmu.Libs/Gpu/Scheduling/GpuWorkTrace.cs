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

    private static readonly (ulong Tick, string Work, int Id)[] Entries = new (ulong, string, int)[Capacity];
    private static int _nextId;

    // The host records a GPU write of a traced item's id after that item (a breadcrumb), and reads
    // back the last id the GPU completed, so a hung tick names the item that never finished.
    public static Action<int>? RecordBreadcrumb { get; set; }
    public static Func<int>? ReadBreadcrumb { get; set; }

    // Records host work (tiling, fault processing) the same way as guest dispatches.
    public static void Traced(string work, Action record)
    {
        if (!Enabled)
        {
            record();
            return;
        }

        var id = Note(work);
        Breadcrumb(-id);
        record();
        Breadcrumb(id);
    }

    public static void Breadcrumb(int id)
    {
        if (Enabled && id != 0)
        {
            RecordBreadcrumb?.Invoke(id);
        }
    }
    private static readonly object Gate = new();
    private static int _next;

    // The tick the host records into now.
    public static Func<ulong>? CurrentTick { get; set; }

    public static void NoteTick(ulong tick, string work)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Entries[_next] = (tick, work, 0);
            _next = (_next + 1) % Capacity;
        }
    }

    // LOCAL ONLY bisection aid: after Arm(), the k-th later traced item (SHARPEMU_DIAG_SLEEP_BEFORE_ITEM)
    // first sleeps, so the GPU work recorded before it can finish before the CPU records the item.
    private static readonly int SleepBeforeItem =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIAG_SLEEP_BEFORE_ITEM"), out var item) ? item : 0;
    private static int _armedCountdown;

    // LOCAL ONLY: SHARPEMU_DIAG_WINDOW_LOG=<dir> logs every command from the armed point for 16 traced items.
    private static readonly string? WindowLogDirectory = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_WINDOW_LOG");
    private static int _windowLogItems;

    public static void ArmSleep()
    {
        if (WindowLogDirectory is not null && _windowLogItems == 0 && !Rendering.FrameCommandLog.Active)
        {
            Rendering.FrameCommandLog.Start(Path.Combine(WindowLogDirectory, $"window-{Environment.TickCount64}"));
            _windowLogItems = 16;
        }

        if (SleepBeforeItem > 0)
        {
            _armedCountdown = SleepBeforeItem;
        }
    }

    private static readonly bool SleepAtEntry = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_SLEEP_AT") == "entry";

    // Dispatch entry, before preparation: sleeps here instead of at the note when so configured.
    public static void MaybeSleepAtEntry()
    {
        if (SleepAtEntry && _armedCountdown == 1)
        {
            _armedCountdown = 0;
            Thread.Sleep(200);
        }
    }

    private static void MaybeSleep()
    {
        if (_windowLogItems > 0 && --_windowLogItems == 0)
        {
            Rendering.FrameCommandLog.Stop();
        }

        if (_armedCountdown > 0 && --_armedCountdown == 0)
        {
            Thread.Sleep(200);
        }
    }

    public static int Note(string work)
    {
        MaybeSleep();
        if (!Enabled || CurrentTick is not { } tick)
        {
            return 0;
        }

        lock (Gate)
        {
            var id = ++_nextId;
            Entries[_next] = (tick(), work, id);
            _next = (_next + 1) % Capacity;
            return id;
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
        if (Enabled && ReadBreadcrumb is { } read)
        {
            Console.Error.WriteLine($"[GPU][ERROR]   last traced item the GPU completed: #{read()}");
        }
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
                var (entryTick, work, id) = Entries[(_next + offset) % Capacity];
                if (work is not null && entryTick == tick)
                {
                    Console.Error.WriteLine($"[GPU][ERROR]   tick {tick}: #{id} {work}");
                    count++;
                }
            }

            Console.Error.WriteLine($"[GPU][ERROR]   tick {tick} carried {count} traced work items");
        }
    }
}
