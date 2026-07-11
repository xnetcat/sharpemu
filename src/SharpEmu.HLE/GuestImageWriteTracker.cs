// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Detects guest CPU writes into memory that backs a host GPU image. On PS5
/// render targets alias unified memory, so games freely mix CPU writes and GPU
/// draws on the same surface (Chowdren titles memset their fog layers every
/// frame). Host GPU images are separate storage, so the video backend needs to
/// know when the guest CPU rewrote a surface to re-upload it. Ranges are
/// write-protected; the first write faults, the fault handler restores write
/// access and marks the range dirty, and the video backend consumes the dirty
/// flag once per flip and re-arms protection after re-uploading.
/// </summary>
public static unsafe class GuestImageWriteTracker
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;

    private sealed class TrackedRange
    {
        public ulong Start;
        public ulong End;
        public int Dirty;
        public int Armed;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, TrackedRange> _rangesByAddress = new();

    // Snapshot array read lock-free from the signal handler; rebuilt on every
    // mutation under the gate. Signal handlers must not take managed locks.
    private static TrackedRange[] _rangeSnapshot = [];

    private static readonly bool _enabled = !OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_CPU_SYNC") != "0";

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    public static bool Enabled => _enabled;

    /// <summary>
    /// Exercises the fault-handling path once outside signal context so every
    /// branch is JIT-compiled (and, under Rosetta 2, translated) before a real
    /// fault arrives — a cold signal path is silently never entered there.
    /// </summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var scratch = NativeMemory.AllocZeroed(4096);
        try
        {
            var address = (ulong)scratch;
            Track(address, 4096);
            _ = TryHandleWriteFault(address);
            _ = ConsumeDirty(address);
            Untrack(address);
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }

    /// <summary>Registers a range and arms write protection on it.</summary>
    public static void Track(ulong address, ulong byteCount)
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return;
        }

        var (start, length) = PageAlign(address, byteCount);
        lock (_gate)
        {
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                range = new TrackedRange
                {
                    Start = start,
                    End = start + length,
                };
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }

            ArmLocked(range);
        }
    }

    public static void Untrack(ulong address)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DisarmLocked(range);
                _rangesByAddress.Remove(address);
                RebuildSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Returns true when the guest CPU wrote the range since the last call,
    /// clearing the flag. The caller re-arms via <see cref="Rearm"/> after it
    /// finished reading the guest bytes.
    /// </summary>
    public static bool ConsumeDirty(ulong address)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            return _rangesByAddress.TryGetValue(address, out var range) &&
                Interlocked.Exchange(ref range.Dirty, 0) != 0;
        }
    }

    public static void Rearm(ulong address)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                ArmLocked(range);
            }
        }
    }

    /// <summary>
    /// Signal-handler entry: if the fault address lies in a tracked, armed
    /// range, restore write access, mark the range dirty, and return true so
    /// the faulting write can be retried. Must not allocate or lock.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress)
    {
        if (!_enabled || faultAddress == 0)
        {
            return false;
        }

        var ranges = Volatile.Read(ref _rangeSnapshot);
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (faultAddress < range.Start || faultAddress >= range.End)
            {
                continue;
            }

            if (Interlocked.Exchange(ref range.Armed, 0) != 0)
            {
                if (Mprotect(
                        (nint)range.Start,
                        (nuint)(range.End - range.Start),
                        ProtRead | ProtWrite) != 0)
                {
                    return false;
                }
            }

            Volatile.Write(ref range.Dirty, 1);
            return true;
        }

        return false;
    }

    private static int _armTraceCount;

    private static void ArmLocked(TrackedRange range)
    {
        if (Interlocked.Exchange(ref range.Armed, 1) == 1)
        {
            return;
        }

        var failed = Mprotect(
            (nint)range.Start,
            (nuint)(range.End - range.Start),
            ProtRead) != 0;
        if (failed)
        {
            Volatile.Write(ref range.Armed, 0);
        }

        if (_armTraceCount < 24)
        {
            Interlocked.Increment(ref _armTraceCount);
            Console.Error.WriteLine(
                $"[WT] arm addr=0x{range.Start:X} bytes={range.End - range.Start} " +
                $"{(failed ? $"FAILED errno={Marshal.GetLastPInvokeError()}" : "ok")}");
        }
    }

    private static void DisarmLocked(TrackedRange range)
    {
        if (Interlocked.Exchange(ref range.Armed, 0) == 1)
        {
            _ = Mprotect(
                (nint)range.Start,
                (nuint)(range.End - range.Start),
                ProtRead | ProtWrite);
        }
    }

    private static void RebuildSnapshotLocked()
    {
        _rangeSnapshot = _rangesByAddress.Values.ToArray();
    }

    private static (ulong Start, ulong Length) PageAlign(ulong address, ulong byteCount)
    {
        const ulong pageMask = 0xFFFUL;
        var start = address & ~pageMask;
        var end = (address + byteCount + pageMask) & ~pageMask;
        return (start, end - start);
    }
}
