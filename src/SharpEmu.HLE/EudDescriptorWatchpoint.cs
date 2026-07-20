// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host.Posix;

namespace SharpEmu.HLE;

/// <summary>
/// Gated diagnostic (SHARPEMU_WATCH_EUD=1) that pins when and by whom the guest
/// populates an Extended-User-Data (EUD) descriptor spill buffer. Menu-scene
/// shaders load constant-buffer V#s indirectly out of an EUD table pointed to by
/// user-data SGPR s28:s29. At inline draw-translation time (on the submit
/// thread) those V# slots frequently decode as garbage — the slot has not been
/// populated with a real V# yet. This watchpoint write-protects the page(s) the
/// unpopulated slot lives in and records every subsequent guest store (faulting
/// RIP, host thread id, monotonic timestamp, offset within the slot/page), so
/// the producer and its timing relative to submit/parse become computable.
///
/// The fault machinery mirrors <see cref="GuestImageWriteTracker"/>: a page is
/// armed read-only, the first store faults into the POSIX signal bridge, the
/// bridge calls <see cref="TryHandleWatchpointFault"/> which restores write
/// access (so the store retries and completes), records the event, and defers
/// re-arming to a managed submit-boundary flush. Everything is capped and
/// auto-disarms after a bounded number of events; the region is hot guest
/// memory, so protection is never left latched. POSIX only; disabled on Windows.
/// </summary>
public static unsafe class EudDescriptorWatchpoint
{
    /// <summary>Reads a guest dword; supplied by the caller that owns CpuContext.</summary>
    public delegate bool GuestDwordReader(ulong address, out uint value);

    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ClockMonotonicRaw = 4;

    private const int MaxWatchedPages = 8;
    private const int MaxEvents = 256;
    private const int MaxArms = 512;
    private const int EventRingLength = 512; // power of two

    private sealed class WatchedPage
    {
        public ulong PageStart;
        public ulong PageEnd;
        public ulong SlotAddress;
        public ulong SlotLength;
        public int Armed;
        public int NeedsRearm;
        public uint SubmitThreadId;
        public long ArmTimestampNanoseconds;
        public long ArmSequence;
        public uint Snapshot0;
        public uint Snapshot1;
        public uint Snapshot2;
        public uint Snapshot3;
        public int Index;
        public string Label = string.Empty;
    }

    private struct EventRecord
    {
        public long Sequence;
        public ulong FaultAddress;
        public ulong Rip;
        public uint HostThreadId;
        public long TimestampNanoseconds;
        public int PageIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static readonly object _gate = new();
    private static readonly WatchedPage[] _pages = new WatchedPage[MaxWatchedPages];

    // Immutable snapshot read lock-free from the signal handler; rebuilt under
    // the gate on every mutation. Signal handlers must not take managed locks.
    private static WatchedPage[] _pageSnapshot = [];
    private static int _pageCount;

    private static readonly EventRecord[] _eventRing = new EventRecord[EventRingLength];
    private static long _eventSequence;
    private static long _flushedSequence;
    private static long _managedWriteCount;
    private static long _armCount;
    private static int _saturated;

    private static readonly bool _enabled = !OperatingSystem.IsWindows() &&
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_WATCH_EUD"),
            "1",
            StringComparison.Ordinal);
    private static readonly long _epochNanoseconds = _enabled ? GetMonotonicNanoseconds() : 0;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = false)]
    private static extern int ClockGetTime(int clockId, Timespec* time);

    public static bool Enabled => _enabled;

    /// <summary>
    /// JIT/Rosetta-warms the signal-context helpers (timestamp + thread id
    /// P/Invokes) so the first real protected-page fault does not have to
    /// translate cold code inside the signal frame.
    /// </summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        _ = GetMonotonicNanoseconds();
        _ = PosixHostStubs.GetCurrentThreadId();
    }

    /// <summary>
    /// Arms a watchpoint on the page(s) holding an unpopulated EUD descriptor
    /// slot. Call from the submit thread at draw-translation time with the
    /// guest address the V# base dword was read out of and the four dwords
    /// currently sitting there. Idempotent per page; re-arms a page that a
    /// prior store disarmed.
    /// </summary>
    public static void Arm(
        ulong slotAddress,
        ulong slotLength,
        string label,
        uint snapshot0,
        uint snapshot1,
        uint snapshot2,
        uint snapshot3)
    {
        if (!_enabled || slotAddress == 0 || Volatile.Read(ref _saturated) != 0)
        {
            return;
        }

        var pageStart = slotAddress & ~0xFFFUL;
        var pageEnd = (slotAddress + Math.Max(slotLength, 1) + 0xFFFUL) & ~0xFFFUL;
        var threadId = PosixHostStubs.GetCurrentThreadId();

        lock (_gate)
        {
            if (Volatile.Read(ref _saturated) != 0)
            {
                return;
            }

            for (var i = 0; i < _pageCount; i++)
            {
                if (_pages[i].PageStart == pageStart)
                {
                    var existing = _pages[i];
                    if (Volatile.Read(ref existing.Armed) == 0 &&
                        Volatile.Read(ref existing.NeedsRearm) != 0)
                    {
                        RearmLocked(existing, "rearm-on-arm");
                    }

                    return;
                }
            }

            if (_pageCount >= MaxWatchedPages || _armCount >= MaxArms)
            {
                return;
            }

            _armCount++;
            var page = new WatchedPage
            {
                PageStart = pageStart,
                PageEnd = pageEnd,
                SlotAddress = slotAddress,
                SlotLength = slotLength,
                SubmitThreadId = threadId,
                ArmTimestampNanoseconds = GetMonotonicNanoseconds(),
                ArmSequence = _armCount,
                Snapshot0 = snapshot0,
                Snapshot1 = snapshot1,
                Snapshot2 = snapshot2,
                Snapshot3 = snapshot3,
                Index = _pageCount,
                Label = label,
            };
            _pages[_pageCount++] = page;
            RebuildSnapshotLocked();

            if (Mprotect((nint)pageStart, (nuint)(pageEnd - pageStart), ProtRead) != 0)
            {
                Volatile.Write(ref page.Armed, 0);
                Log($"[EUD-WATCH][ARM-FAIL] {Describe(page)} errno={Marshal.GetLastPInvokeError()}");
            }
            else
            {
                Volatile.Write(ref page.Armed, 1);
                Log($"[EUD-WATCH][ARM] {Describe(page)}");
            }
        }
    }

    public readonly struct ManagedWriteScope : IDisposable
    {
        private readonly bool _entered;

        internal ManagedWriteScope(bool entered)
        {
            _entered = entered;
        }

        public void Dispose()
        {
            if (_entered)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    /// <summary>
    /// Managed guest writes (e.g. a deferred WRITE_DATA side-effect running on
    /// the presenter thread) copy into guest memory with Buffer.MemoryCopy,
    /// which a protected page turns into a fatal AccessViolation instead of a
    /// resumable signal fault. Unprotect any watched page in the write span and
    /// hold the gate until the copy completes so a concurrent re-arm cannot
    /// re-protect mid-copy. A managed write into a watched slot is itself a
    /// producer event and is recorded.
    /// </summary>
    public static ManagedWriteScope BeginManagedWrite(ulong address, ulong byteCount)
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return default;
        }

        var pages = Volatile.Read(ref _pageSnapshot);
        if (pages.Length == 0)
        {
            return default;
        }

        var end = address > ulong.MaxValue - byteCount ? ulong.MaxValue : address + byteCount;
        var overlaps = false;
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            if (address < page.PageEnd && end > page.PageStart)
            {
                overlaps = true;
                break;
            }
        }

        if (!overlaps)
        {
            return default;
        }

        Monitor.Enter(_gate);
        for (var i = 0; i < _pageCount; i++)
        {
            var page = _pages[i];
            if (address >= page.PageEnd || end <= page.PageStart)
            {
                continue;
            }

            _ = Mprotect(
                (nint)page.PageStart,
                (nuint)(page.PageEnd - page.PageStart),
                ProtRead | ProtWrite);
            var wasArmed = Interlocked.Exchange(ref page.Armed, 0) != 0;
            Volatile.Write(ref page.NeedsRearm, 1);
            if (wasArmed)
            {
                RecordManagedWriteLocked(page, address, byteCount);
            }
        }

        return new ManagedWriteScope(true);
    }

    private static void RecordManagedWriteLocked(WatchedPage page, ulong address, ulong byteCount)
    {
        var threadId = PosixHostStubs.GetCurrentThreadId();
        var now = GetMonotonicNanoseconds();
        var deltaFromSlot = (long)address - (long)page.SlotAddress;
        var writeEnd = address + byteCount;
        var touchesSlot = address < page.SlotAddress + page.SlotLength && writeEnd > page.SlotAddress;
        var latencyMs = (now - page.ArmTimestampNanoseconds) / 1_000_000.0;
        var sequence = Interlocked.Increment(ref _managedWriteCount);
        Log($"[EUD-WATCH][MANAGED] mseq={sequence} {ElapsedMs(now)} page#{page.Index} " +
            $"slot=0x{page.SlotAddress:X16} write=0x{address:X16}+0x{byteCount:X} " +
            $"slot_delta={deltaFromSlot} touches_slot={(touchesSlot ? 1 : 0)} " +
            $"host_tid={threadId} submit_tid={page.SubmitThreadId} " +
            $"same_thread={(threadId == page.SubmitThreadId ? 1 : 0)} " +
            $"latency_since_arm_ms={latencyMs:F3} label={page.Label}");
        if (sequence >= MaxEvents)
        {
            Volatile.Write(ref _saturated, 1);
        }
    }

    /// <summary>
    /// Signal-handler entry: if the fault address lies in a watched page,
    /// restore write access so the store retries, record the store (only on the
    /// armed→disarmed transition), and return true. Must not allocate or lock.
    /// </summary>
    public static bool TryHandleWatchpointFault(ulong faultAddress, ulong rip)
    {
        if (!_enabled)
        {
            return false;
        }

        var pages = Volatile.Read(ref _pageSnapshot);
        WatchedPage? hit = null;
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            if (faultAddress >= page.PageStart && faultAddress < page.PageEnd)
            {
                hit = page;
                break;
            }
        }

        if (hit is null)
        {
            return false;
        }

        // Restore write access first so any concurrent store to the same page
        // is also recovered (idempotent if already writable). Only the store
        // that flips Armed→0 records an event.
        _ = Mprotect(
            (nint)hit.PageStart,
            (nuint)(hit.PageEnd - hit.PageStart),
            ProtRead | ProtWrite);
        if (Interlocked.Exchange(ref hit.Armed, 0) == 0)
        {
            return true;
        }

        Volatile.Write(ref hit.NeedsRearm, 1);
        var threadId = PosixHostStubs.GetCurrentThreadId();
        var sequence = Interlocked.Increment(ref _eventSequence);
        ref var record = ref _eventRing[(int)((sequence - 1) & (EventRingLength - 1))];
        record.FaultAddress = faultAddress;
        record.Rip = rip;
        record.HostThreadId = threadId;
        record.TimestampNanoseconds = GetMonotonicNanoseconds();
        record.PageIndex = hit.Index;
        Volatile.Write(ref record.Sequence, sequence);

        if (sequence >= MaxEvents)
        {
            Volatile.Write(ref _saturated, 1);
        }

        return true;
    }

    /// <summary>
    /// Managed submit-boundary hook: prints a boundary marker, drains captured
    /// store events, re-reads each disarmed slot to detect a silent (aliased or
    /// managed) population, and re-arms disarmed pages. Auto-disarms everything
    /// once the event cap is hit. Call from ordinary managed execution only.
    /// </summary>
    public static void OnSubmitBoundary(GuestDwordReader reader, string label)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_pageCount == 0)
            {
                return;
            }

            var threadId = PosixHostStubs.GetCurrentThreadId();
            Log($"[EUD-WATCH][SUBMIT] label={label} host_tid={threadId} " +
                $"pages={_pageCount} events={Volatile.Read(ref _eventSequence)}");

            DrainEventsLocked();

            if (Volatile.Read(ref _saturated) != 0)
            {
                DisarmAllLocked("saturated");
                _pageCount = 0;
                RebuildSnapshotLocked();
                return;
            }

            for (var i = 0; i < _pageCount; i++)
            {
                var page = _pages[i];
                if (Volatile.Read(ref page.Armed) != 0 ||
                    Volatile.Read(ref page.NeedsRearm) == 0)
                {
                    continue;
                }

                ReReadSlotLocked(page, reader);
                RearmLocked(page, "rearm-boundary");
            }
        }
    }

    private static void DrainEventsLocked()
    {
        var end = Volatile.Read(ref _eventSequence);
        var start = _flushedSequence + 1;
        if (start > end)
        {
            return;
        }

        if (end - start >= EventRingLength)
        {
            start = end - EventRingLength + 1;
        }

        for (var sequence = start; sequence <= end; sequence++)
        {
            ref var record = ref _eventRing[(int)((sequence - 1) & (EventRingLength - 1))];
            if (Volatile.Read(ref record.Sequence) != sequence)
            {
                continue;
            }

            var page = record.PageIndex < _pageCount ? _pages[record.PageIndex] : null;
            var slotAddress = page?.SlotAddress ?? 0;
            var slotLength = page?.SlotLength ?? 0;
            var deltaFromSlot = (long)record.FaultAddress - (long)slotAddress;
            var inSlot = slotLength != 0 && deltaFromSlot >= 0 && (ulong)deltaFromSlot < slotLength;
            var pageOffset = page is null ? 0 : record.FaultAddress - page.PageStart;
            var sameThreadAsSubmit = page is not null && record.HostThreadId == page.SubmitThreadId;
            var latencyMs = page is null
                ? 0.0
                : (record.TimestampNanoseconds - page.ArmTimestampNanoseconds) / 1_000_000.0;

            Log($"[EUD-WATCH][EVENT] seq={sequence} {ElapsedMs(record.TimestampNanoseconds)} " +
                $"page#{record.PageIndex} slot=0x{slotAddress:X16} " +
                $"fault=0x{record.FaultAddress:X16} page_off=0x{pageOffset:X} " +
                $"slot_delta={deltaFromSlot} in_slot={(inSlot ? 1 : 0)} " +
                $"rip=0x{record.Rip:X16} host_tid={record.HostThreadId} " +
                $"submit_tid={(page?.SubmitThreadId ?? 0)} same_thread={(sameThreadAsSubmit ? 1 : 0)} " +
                $"latency_since_arm_ms={latencyMs:F3} label={page?.Label}");
        }

        _flushedSequence = end;
    }

    private static void ReReadSlotLocked(WatchedPage page, GuestDwordReader reader)
    {
        reader(page.SlotAddress, out var word0);
        reader(page.SlotAddress + 4, out var word1);
        reader(page.SlotAddress + 8, out var word2);
        reader(page.SlotAddress + 12, out var word3);
        var changed = word0 != page.Snapshot0 || word1 != page.Snapshot1 ||
                      word2 != page.Snapshot2 || word3 != page.Snapshot3;
        if (changed)
        {
            Log($"[EUD-WATCH][SLOTCHANGE] page#{page.Index} slot=0x{page.SlotAddress:X16} " +
                $"was=[{page.Snapshot0:X8}:{page.Snapshot1:X8}:{page.Snapshot2:X8}:{page.Snapshot3:X8}] " +
                $"now=[{word0:X8}:{word1:X8}:{word2:X8}:{word3:X8}]");
            page.Snapshot0 = word0;
            page.Snapshot1 = word1;
            page.Snapshot2 = word2;
            page.Snapshot3 = word3;
        }
    }

    private static void RearmLocked(WatchedPage page, string operation)
    {
        Volatile.Write(ref page.NeedsRearm, 0);
        if (Mprotect(
                (nint)page.PageStart,
                (nuint)(page.PageEnd - page.PageStart),
                ProtRead) != 0)
        {
            Volatile.Write(ref page.Armed, 0);
            Log($"[EUD-WATCH][{operation}-FAIL] page#{page.Index} errno={Marshal.GetLastPInvokeError()}");
            return;
        }

        Volatile.Write(ref page.Armed, 1);
    }

    private static void DisarmAllLocked(string reason)
    {
        for (var i = 0; i < _pageCount; i++)
        {
            var page = _pages[i];
            _ = Mprotect(
                (nint)page.PageStart,
                (nuint)(page.PageEnd - page.PageStart),
                ProtRead | ProtWrite);
            Volatile.Write(ref page.Armed, 0);
        }

        Log($"[EUD-WATCH][DISARM] reason={reason} events={Volatile.Read(ref _eventSequence)} " +
            $"pages={_pageCount}");
    }

    private static void RebuildSnapshotLocked()
    {
        var snapshot = new WatchedPage[_pageCount];
        Array.Copy(_pages, snapshot, _pageCount);
        _pageSnapshot = snapshot;
    }

    private static string Describe(WatchedPage page) =>
        $"page#{page.Index} arm#{page.ArmSequence} {ElapsedMs(page.ArmTimestampNanoseconds)} " +
        $"slot=0x{page.SlotAddress:X16}+0x{page.SlotLength:X} " +
        $"page=0x{page.PageStart:X16}..0x{page.PageEnd:X16} submit_tid={page.SubmitThreadId} " +
        $"snap=[{page.Snapshot0:X8}:{page.Snapshot1:X8}:{page.Snapshot2:X8}:{page.Snapshot3:X8}] " +
        $"label={page.Label}";

    private static string ElapsedMs(long timestampNanoseconds) =>
        $"t_ms={(timestampNanoseconds - _epochNanoseconds) / 1_000_000.0:F3}";

    private static void Log(string message) => Console.Error.WriteLine(message);

    private static long GetMonotonicNanoseconds()
    {
        Timespec time;
        return ClockGetTime(ClockMonotonicRaw, &time) == 0
            ? unchecked((time.Seconds * 1_000_000_000L) + time.Nanoseconds)
            : 0;
    }
}
