// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Core.Memory;

// LOCAL ONLY diagnostic (SHARPEMU_PERF_HLE=1): splits the cost of managed guest-memory accesses
// (every HLE argument read and result write) into the tracker notifications and the locked copy.
internal static class MemoryAccessProfile
{
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("SHARPEMU_PERF_HLE") == "1";

    private static long _writes, _writeNotifyTicks, _writeMarkTicks, _writeCopyTicks;
    private static long _reads, _readTicks;

    public static void RecordWrite(long notify, long mark, long copy)
    {
        Interlocked.Increment(ref _writes);
        Interlocked.Add(ref _writeNotifyTicks, notify);
        Interlocked.Add(ref _writeMarkTicks, mark);
        Interlocked.Add(ref _writeCopyTicks, copy);
    }

    public static void RecordRead(long ticks)
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _readTicks, ticks);
    }

    public static string Report()
    {
        static double Us(long ticks, long count) => count == 0 ? 0 : ticks * 1e6 / Stopwatch.Frequency / count;
        var writes = Interlocked.Exchange(ref _writes, 0);
        var notify = Interlocked.Exchange(ref _writeNotifyTicks, 0);
        var mark = Interlocked.Exchange(ref _writeMarkTicks, 0);
        var copy = Interlocked.Exchange(ref _writeCopyTicks, 0);
        var reads = Interlocked.Exchange(ref _reads, 0);
        var read = Interlocked.Exchange(ref _readTicks, 0);
        return $"writes={writes} image_notify_us={Us(notify, writes):F2} gpu_mark_us={Us(mark, writes):F2} " +
            $"locked_copy_us={Us(copy, writes):F2} reads={reads} read_us={Us(read, reads):F2}";
    }
}
