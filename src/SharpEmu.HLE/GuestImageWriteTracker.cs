// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
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
    private const int ClockMonotonicRaw = 4;

    private sealed class TrackedRange
    {
        public ulong Address;
        public ulong ByteCount;
        public ulong Start;
        public ulong End;
        public int Dirty;
        public int Armed;
        // Set when an arm was skipped because a managed guest write held a pin
        // over this range; the last pin to drain re-arms it.
        public int ArmDeferred;
        public int FirstCpuWriteSeen;
        public int PendingFirstCpuWrite;
        public long WriteGeneration;
        public bool TraceLifetime;
        public long SourceSequence;
        public long FirstCpuWriteTraceSequence;
        public long FirstCpuWriteTimestampNanoseconds;
        public ulong FirstCpuWriteAddress;
        public ulong FirstCpuWritePage;
        public string Source = "unspecified";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, TrackedRange> _rangesByAddress = new();

    // ---- managed-write lease -----------------------------------------------
    //
    // Managed guest writes copy with Buffer.MemoryCopy. If the tracker drops
    // write access on the destination at any instant during that copy the
    // runtime raises a fatal, non-resumable AccessViolationException (a native
    // guest store faults into the POSIX signal bridge instead and recovers).
    // So arming must be excluded from managed writes outright: noticing the
    // clash afterwards and restoring the protection cannot un-fault a copy that
    // already ran.
    //
    // _writeLease is a shared/exclusive lease over the tracker's mprotect
    // calls. Managed writes take the shared side for the whole copy;
    // ArmLocked takes the exclusive side around its mprotect.
    //
    // LOCK ORDER (never invert):
    //     GuestImageWriteTracker._gate
    //       -> _writeLease (exclusive, taken by ArmLocked)
    //     _writeLease (shared, taken by BeginManagedWrite)
    //       -> PhysicalVirtualMemory._gate
    //       -> PosixHostMemory.Gate
    // A shared holder never takes _gate: BeginManagedWrite is lock-free, and
    // EndManagedWrite releases its share before it takes _gate. Every caller
    // takes the shared lease before PhysicalVirtualMemory's gate and releases
    // it after (see PhysicalVirtualMemory.TryWrite/TryCopy), so a thread
    // holding a memory gate never waits on the lease.
    private const int LeaseExclusiveHeld = 1 << 30;
    private const int LeaseExclusiveWaiting = 1 << 29;
    private const int LeaseSharedMask = LeaseExclusiveWaiting - 1;
    private static int _writeLease;

    // Shared leases held by this thread. Only used to detect re-entrancy: a
    // thread that arms while inside its own managed write must not wait for
    // itself (deadlock) and must not protect the page it is copying into.
    [ThreadStatic]
    private static int _threadWriteLeases;

    // Ranges whose arm was skipped by that re-entrancy guard, so the drain in
    // EndManagedWrite can skip the range walk entirely in the common case.
    private static int _armDeferredCount;

    /// <summary>Immutable snapshot read lock-free from the signal handler and
    /// the managed-write pre-visit; rebuilt on every mutation under the gate
    /// (signal handlers must not take managed locks). Carrying the overall
    /// bounds inside the same object keeps the hot-path intersection test
    /// consistent with the array it guards.</summary>
    private sealed class RangeSnapshot
    {
        public static readonly RangeSnapshot Empty = new([]);

        public readonly TrackedRange[] Ranges;
        public readonly ulong Start;
        public readonly ulong End;

        public RangeSnapshot(TrackedRange[] ranges)
        {
            Ranges = ranges;
            Start = ulong.MaxValue;
            End = 0;
            foreach (var range in ranges)
            {
                Start = Math.Min(Start, range.Start);
                End = Math.Max(End, range.End);
            }
        }
    }

    private static RangeSnapshot _rangeSnapshot = RangeSnapshot.Empty;

    private static readonly bool _enabled = !OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_CPU_SYNC") != "0";
    private static readonly (bool Wildcard, ulong[] Addresses) _lifetimeTraceFilter =
        ParseAddressList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
    private static readonly (bool Wildcard, string[] Sources) _lifetimeSourceTraceFilter =
        ParseSourceList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_MEMORY_LIFETIME"));
    private static readonly bool _lifetimeTraceEnabled =
        _lifetimeTraceFilter.Wildcard ||
        _lifetimeTraceFilter.Addresses.Length != 0 ||
        _lifetimeSourceTraceFilter.Wildcard ||
        _lifetimeSourceTraceFilter.Sources.Length != 0;
    private static readonly long _lifetimeTraceEpochNanoseconds =
        _enabled && _lifetimeTraceEnabled ? GetMonotonicNanoseconds() : 0;
    private static long _lifetimeTraceSequence;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = false)]
    private static extern int ClockGetTime(int clockId, Timespec* time);

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
            // Warm the timestamp P/Invoke used by the signal-safe scalar
            // capture path before a real protected-page write reaches it.
            _ = GetMonotonicNanoseconds();
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
    public static void Track(
        ulong address,
        ulong byteCount,
        long sourceSequence = 0,
        string source = "unspecified")
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return;
        }

        var (start, length) = PageAlign(address, byteCount);
        lock (_gate)
        {
            _rangesByAddress.TryGetValue(address, out var range);
            if (range is not null &&
                (range.Start != start ||
                 range.End != start + length ||
                 range.ByteCount != byteCount))
            {
                // Never resize an object that is still reachable from the
                // signal handler's lock-free snapshot. Retire it and publish
                // a fresh immutable range, carrying the write generation so
                // resizes do not hide guest CPU rewrites from cache owners.
                var writeGeneration = Volatile.Read(ref range.WriteGeneration);
                DisarmLocked(range, "replace-range");
                _rangesByAddress.Remove(address);
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = start,
                    End = start + length,
                    WriteGeneration = writeGeneration,
                };
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }

            if (range is null)
            {
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = start,
                    End = start + length,
                    TraceLifetime =
                        ShouldTraceRange(start, start + length) || ShouldTraceSource(source),
                    SourceSequence = sourceSequence,
                    Source = source,
                };
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }
            else
            {
                FlushPendingFirstCpuWrite(range);
            }

            range.SourceSequence = sourceSequence;
            range.Source = source;
            range.TraceLifetime =
                ShouldTraceRange(range.Start, range.End) || ShouldTraceSource(source);
            ArmLocked(range, "arm");
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
                DisarmLocked(range, "untrack");
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
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            FlushPendingFirstCpuWrite(range);
            return Interlocked.Exchange(ref range.Dirty, 0) != 0;
        }
    }

    /// <summary>
    /// Non-consuming variant of <see cref="ConsumeDirty"/>: reports whether
    /// the range has been written since it was last re-armed, leaving the
    /// flag for the owner that evicts and re-uploads.
    /// </summary>
    public static bool PeekDirty(ulong address)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            FlushPendingFirstCpuWrite(range);
            return Volatile.Read(ref range.Dirty) != 0;
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
                ArmLocked(range, "rearm");
            }
        }
    }

    /// <summary>
    /// Returns the monotonic first-write generation for a tracked allocation.
    /// Unlike the consuming dirty flag, this remains changed after another
    /// cache owner consumes and re-arms the range.
    /// </summary>
    public static bool TryGetWriteGeneration(ulong address, out long generation)
    {
        generation = 0;
        if (!_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            generation = Volatile.Read(ref range.WriteGeneration);
            return true;
        }
    }

    /// <summary>
    /// Prepares pages touched by a managed HLE memory write. Native guest
    /// stores fault and enter <see cref="TryHandleWriteFault"/> through the
    /// POSIX signal bridge, but a managed Buffer.MemoryCopy into a protected
    /// page is surfaced by the runtime as a fatal AccessViolation instead of
    /// a resumable guest fault. Visit every page in the write span up front so
    /// all overlapping texture owners are dirtied and made writable.
    /// </summary>
    public static void NotifyManagedWrite(ulong address, ulong byteCount)
    {
        if (BeginManagedWrite(address, byteCount))
        {
            EndManagedWrite();
        }
    }

    /// <summary>
    /// Unprotects any tracked pages the write covers and leases them writable
    /// for the duration of the copy. Returns true when a lease was taken, in
    /// which case the caller must call <see cref="EndManagedWrite"/> in a
    /// finally.
    /// </summary>
    /// <remarks>
    /// Disarming before the copy is not enough on its own, and neither is a
    /// flag the armer merely consults: arming runs from GPU threads under a
    /// lock unrelated to the caller's, and its protection change is an mprotect
    /// syscall. An armer that checks for in-flight writes, misses one, and then
    /// mprotects has already made the page read-only for as long as it takes to
    /// notice and undo it — and a managed copy that faults in that window dies
    /// with a fatal AccessViolationException that no later repair can take
    /// back. The lease excludes the two outright: while it is held
    /// <see cref="ArmLocked"/> cannot run its mprotect at all.
    /// </remarks>
    public static bool BeginManagedWrite(ulong address, ulong byteCount) =>
        BeginManagedWrite(address, byteCount, out _);

    /// <inheritdoc cref="BeginManagedWrite(ulong,ulong)"/>
    /// <param name="pagesWritable">
    /// False when the tracker holds a lease but could not restore write access
    /// (the mprotect itself failed). The caller must not copy directly in that
    /// case — the page really is read-only and a managed store would be fatal —
    /// and must instead take a path that changes the protection itself.
    /// </param>
    public static bool BeginManagedWrite(ulong address, ulong byteCount, out bool pagesWritable)
    {
        pagesWritable = true;
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return false;
        }

        var end = address > ulong.MaxValue - byteCount
            ? ulong.MaxValue
            : address + byteCount;

        // Fast rejection for the hot path: this runs on every managed guest
        // write, and almost none of them touch tracked texture pages. The
        // bounds live inside the snapshot so they are always consistent with
        // the ranges the per-page visit below would consult. Only writes that
        // actually overlap tracked pages pay for the lease.
        if (!OverlapsTrackedBounds(address, end))
        {
            return false;
        }

        AcquireWriteLeaseShared();

        // Re-test under the lease. Track publishes a new range into the
        // snapshot before ArmLocked protects it, and that arm is now blocked
        // behind this lease, so a range that appeared between the two tests is
        // visible here and still writable.
        if (!OverlapsTrackedBounds(address, end))
        {
            ReleaseWriteLeaseShared();
            return false;
        }

        var candidate = address;
        while (candidate < end)
        {
            if (VisitWriteFault(candidate) == WriteFaultOutcome.UnprotectFailed)
            {
                pagesWritable = false;
            }

            var nextPage = (candidate & ~0xFFFUL) + 0x1000UL;
            if (nextPage <= candidate)
            {
                break;
            }
            candidate = nextPage;
        }

        return true;
    }

    private static bool OverlapsTrackedBounds(ulong address, ulong end)
    {
        var snapshot = Volatile.Read(ref _rangeSnapshot);
        return snapshot.Ranges.Length != 0 && end > snapshot.Start && address < snapshot.End;
    }

    /// <summary>
    /// Releases a lease taken by <see cref="BeginManagedWrite"/>, re-arming any
    /// range whose arm was skipped by the re-entrancy guard.
    /// </summary>
    public static void EndManagedWrite()
    {
        ReleaseWriteLeaseShared();

        // Only the outermost release may re-arm: an inner one would protect a
        // page this thread is still copying into.
        if (_threadWriteLeases != 0 || Volatile.Read(ref _armDeferredCount) == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var range in _rangesByAddress.Values)
            {
                if (Interlocked.Exchange(ref range.ArmDeferred, 0) == 1)
                {
                    Interlocked.Decrement(ref _armDeferredCount);
                    ArmLocked(range, "rearm-deferred");
                }
            }
        }
    }

    private static void AcquireWriteLeaseShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            var observed = Volatile.Read(ref _writeLease);
            if ((observed & (LeaseExclusiveHeld | LeaseExclusiveWaiting)) == 0 &&
                Interlocked.CompareExchange(ref _writeLease, observed + 1, observed) == observed)
            {
                _threadWriteLeases++;
                return;
            }

            // An arm is in progress or queued. Waiting here (rather than
            // racing it) is what keeps the page's protection stable for the
            // copy, and it also stops a steady stream of writes from starving
            // arming — which would silently disable CPU-write detection.
            //
            // The exclusive holder only runs a single mprotect and never
            // blocks, so this resolves in microseconds; -1 keeps SpinWait from
            // escalating to a millisecond sleep and turning a guest write into
            // a scheduling stall.
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    private static void ReleaseWriteLeaseShared()
    {
        _threadWriteLeases--;
        Interlocked.Decrement(ref _writeLease);
    }

    /// <summary>
    /// Takes the exclusive side of the write lease so no managed guest write is
    /// copying while the caller changes page protection. Returns false when the
    /// calling thread already holds a shared lease, which means it is arming
    /// from inside its own managed write: protecting the page would fault that
    /// copy and waiting would deadlock on itself, so the caller must defer.
    /// </summary>
    private static bool TryAcquireWriteLeaseExclusive()
    {
        if (_threadWriteLeases > 0)
        {
            return false;
        }

        // Only ArmLocked takes the exclusive side, and it always runs under
        // _gate, so there is never a second exclusive acquirer to contend with.
        var spin = new SpinWait();
        while (true)
        {
            var observed = Volatile.Read(ref _writeLease);
            if (Interlocked.CompareExchange(
                    ref _writeLease,
                    observed | LeaseExclusiveWaiting,
                    observed) == observed)
            {
                break;
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }

        // The waiting bit is published, so no new shared lease can be granted;
        // drain the ones already in flight. Each holder only has a bounded copy
        // left to run and never blocks on the tracker, so this terminates.
        spin = new SpinWait();
        while (true)
        {
            var observed = Volatile.Read(ref _writeLease);
            if ((observed & LeaseSharedMask) == 0 &&
                Interlocked.CompareExchange(
                    ref _writeLease,
                    (observed & ~LeaseExclusiveWaiting) | LeaseExclusiveHeld,
                    observed) == observed)
            {
                return true;
            }

            spin.SpinOnce();
        }
    }

    private static void ReleaseWriteLeaseExclusive() =>
        Interlocked.Add(ref _writeLease, -LeaseExclusiveHeld);

    /// <summary>
    /// Flushes scalar first-write records captured by the POSIX signal handler.
    /// Call only from ordinary managed execution, never from signal context.
    /// </summary>
    public static void FlushPendingDiagnostics()
    {
        if (!_enabled || !_lifetimeTraceEnabled)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var range in _rangesByAddress.Values)
            {
                FlushPendingFirstCpuWrite(range);
            }
        }
    }

    /// <summary>
    /// Signal-handler entry: if the fault address lies in a tracked, armed
    /// range, restore write access, mark the range dirty, and return true so
    /// the faulting write can be retried. Must not allocate or lock.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress) =>
        VisitWriteFault(faultAddress) == WriteFaultOutcome.Writable;

    private enum WriteFaultOutcome
    {
        /// <summary>No tracked range covers the address.</summary>
        NotTracked,

        /// <summary>The address is covered and its pages are writable.</summary>
        Writable,

        /// <summary>The address is covered but the mprotect failed, so the
        /// pages are still read-only.</summary>
        UnprotectFailed,
    }

    /// <summary>
    /// Shared core of <see cref="TryHandleWriteFault"/> and
    /// <see cref="BeginManagedWrite"/>. Distinguishes "not tracked" from
    /// "tracked but could not be unprotected" so a managed writer can react to
    /// the second instead of copying into a page it has been told is protected.
    /// Must not allocate or lock: this runs in signal context.
    /// </summary>
    private static WriteFaultOutcome VisitWriteFault(ulong faultAddress)
    {
        if (!_enabled || faultAddress == 0)
        {
            return WriteFaultOutcome.NotTracked;
        }

        var ranges = Volatile.Read(ref _rangeSnapshot).Ranges;
        var writableStart = ulong.MaxValue;
        var writableEnd = 0UL;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (faultAddress < range.Start || faultAddress >= range.End)
            {
                continue;
            }

            writableStart = Math.Min(writableStart, range.Start);
            writableEnd = Math.Max(writableEnd, range.End);
        }

        if (writableStart == ulong.MaxValue)
        {
            return WriteFaultOutcome.NotTracked;
        }

        // Ranges are page-aligned and may overlap (font atlases and other
        // suballocations commonly share pages). Unprotecting one range also
        // makes every overlapping tracked page writable. Expand to the full
        // transitive overlap and dirty/disarm every owner, otherwise only the
        // first dictionary entry observes the write and the others retain a
        // stale cached texture indefinitely.
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                if (range.Start >= writableEnd || range.End <= writableStart)
                {
                    continue;
                }

                var start = Math.Min(writableStart, range.Start);
                var end = Math.Max(writableEnd, range.End);
                if (start != writableStart || end != writableEnd)
                {
                    writableStart = start;
                    writableEnd = end;
                    expanded = true;
                }
            }
        }

        var needsUnprotect = false;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Start < writableEnd && range.End > writableStart &&
                Volatile.Read(ref range.Armed) != 0)
            {
                needsUnprotect = true;
                break;
            }
        }

        if (needsUnprotect &&
            Mprotect(
                (nint)writableStart,
                (nuint)(writableEnd - writableStart),
                ProtRead | ProtWrite) != 0)
        {
            // Leave Armed set: the pages really are still read-only, so the
            // tracker's belief stays true and a managed writer is told to take
            // a path that changes the protection itself.
            return WriteFaultOutcome.UnprotectFailed;
        }

        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Start >= writableEnd || range.End <= writableStart)
            {
                continue;
            }

            var wasArmed = Interlocked.Exchange(ref range.Armed, 0) != 0;
            if (wasArmed)
            {
                Interlocked.Increment(ref range.WriteGeneration);
            }
            if (wasArmed &&
                range.TraceLifetime &&
                Interlocked.CompareExchange(ref range.FirstCpuWriteSeen, 1, 0) == 0)
            {
                // Signal context: capture preallocated scalar fields only.
                // Formatting and I/O are deferred to a locked safe path.
                range.FirstCpuWriteTraceSequence =
                    Interlocked.Increment(ref _lifetimeTraceSequence);
                range.FirstCpuWriteTimestampNanoseconds = GetMonotonicNanoseconds();
                range.FirstCpuWriteAddress = faultAddress;
                range.FirstCpuWritePage = faultAddress & ~0xFFFUL;
                Volatile.Write(ref range.PendingFirstCpuWrite, 1);
                Volatile.Write(ref range.FirstCpuWriteSeen, 2);
            }

            Volatile.Write(ref range.Dirty, 1);
        }

        return WriteFaultOutcome.Writable;
    }

    private static void ArmLocked(TrackedRange range, string operation)
    {
        FlushPendingFirstCpuWrite(range);

        // Cheap early-out so a re-arm of an already-armed range (the common
        // per-flip case) never pays for the lease. The authoritative test is
        // repeated under the lease below.
        if (Volatile.Read(ref range.Armed) == 1)
        {
            return;
        }

        // Take the exclusive side of the write lease so the mprotect below
        // cannot land inside a managed guest write's copy. This waits rather
        // than checking-and-retracting: retracting a protection after the fact
        // does nothing for a copy that already faulted, and a managed fault is
        // an unrecoverable AccessViolationException.
        if (!TryAcquireWriteLeaseExclusive())
        {
            // This thread is itself inside a managed guest write. Protecting
            // now would fault our own copy, and waiting would deadlock on
            // ourselves; the outermost EndManagedWrite re-arms instead.
            if (Interlocked.Exchange(ref range.ArmDeferred, 1) == 0)
            {
                Interlocked.Increment(ref _armDeferredCount);
            }

            return;
        }

        bool failed;
        try
        {
            // Claim the range only now. Setting Armed before the lease was
            // acquired let a managed write that was already inside its own
            // lease observe the range as armed, unprotect it (a no-op, the page
            // was still writable) and clear Armed - after which this mprotect
            // still ran and left a protected page recorded as disarmed. The
            // next managed write then trusted the flag, skipped the unprotect
            // and copied into a read-only page.
            if (Interlocked.Exchange(ref range.Armed, 1) == 1)
            {
                return;
            }

            // A new publication/rearm starts a new first-write lifetime.
            Volatile.Write(ref range.FirstCpuWriteSeen, 0);
            failed = Mprotect(
                (nint)range.Start,
                (nuint)(range.End - range.Start),
                ProtRead) != 0;
            if (failed)
            {
                Volatile.Write(ref range.Armed, 0);
            }
            else if (Volatile.Read(ref range.Armed) == 0)
            {
                // The POSIX fault handler runs in signal context and so cannot
                // take the lease; it can disarm an overlapping range while this
                // arm is in flight. Restoring write access keeps the flag and
                // the hardware in agreement - the direction that only ever adds
                // access, so it can never fault anyone.
                _ = Mprotect(
                    (nint)range.Start,
                    (nuint)(range.End - range.Start),
                    ProtRead | ProtWrite);
            }
        }
        finally
        {
            ReleaseWriteLeaseExclusive();
        }

        if (range.TraceLifetime)
        {
            TraceLifetime(
                range,
                failed ? $"{operation}-failed-errno-{Marshal.GetLastPInvokeError()}" : operation);
        }
    }

    private static void DisarmLocked(TrackedRange range, string operation)
    {
        FlushPendingFirstCpuWrite(range);

        // A pending re-arm dies with the disarm; leaving the flag set would
        // keep _armDeferredCount positive forever and make every managed write
        // take the gate on release.
        if (Interlocked.Exchange(ref range.ArmDeferred, 0) == 1)
        {
            Interlocked.Decrement(ref _armDeferredCount);
        }

        var wasArmed = Interlocked.Exchange(ref range.Armed, 0) == 1;
        if (wasArmed)
        {
            // No lease needed: this only adds write access, which can never
            // fault an in-flight copy, and it is serialised against ArmLocked
            // by _gate.
            _ = Mprotect(
                (nint)range.Start,
                (nuint)(range.End - range.Start),
                ProtRead | ProtWrite);
        }

        if (range.TraceLifetime)
        {
            TraceLifetime(range, wasArmed ? operation : $"{operation}-already-disarmed");
        }
    }

    private static void RebuildSnapshotLocked()
    {
        Volatile.Write(ref _rangeSnapshot, new RangeSnapshot(_rangesByAddress.Values.ToArray()));
    }

    private static (ulong Start, ulong Length) PageAlign(ulong address, ulong byteCount)
    {
        const ulong pageMask = 0xFFFUL;
        var start = address & ~pageMask;
        var end = (address + byteCount + pageMask) & ~pageMask;
        return (start, end - start);
    }

    private static bool ShouldTraceRange(ulong start, ulong end)
    {
        if (_lifetimeTraceFilter.Wildcard)
        {
            return true;
        }

        var addresses = _lifetimeTraceFilter.Addresses;
        for (var index = 0; index < addresses.Length; index++)
        {
            if (addresses[index] >= start && addresses[index] < end)
            {
                return true;
            }
        }

        return false;
    }

    private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return (false, []);
        }

        var parsedAddresses = new List<ulong>();
        foreach (var token in addresses.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*")
            {
                return (true, []);
            }

            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                parsedAddresses.Add(parsed);
            }
        }

        return (false, parsedAddresses.ToArray());
    }

    private static bool ShouldTraceSource(string source)
    {
        if (_lifetimeSourceTraceFilter.Wildcard)
        {
            return true;
        }

        return Array.IndexOf(_lifetimeSourceTraceFilter.Sources, source) >= 0;
    }

    private static (bool Wildcard, string[] Sources) ParseSourceList(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
        {
            return (false, []);
        }

        var parsedSources = new List<string>();
        foreach (var token in sources.Split(
                     [',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*")
            {
                return (true, []);
            }

            parsedSources.Add(token);
        }

        return (false, parsedSources.ToArray());
    }

    private static void FlushPendingFirstCpuWrite(TrackedRange range)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref range.FirstCpuWriteSeen) == 1)
        {
            spin.SpinOnce();
        }

        if (!range.TraceLifetime || Interlocked.Exchange(ref range.PendingFirstCpuWrite, 0) == 0)
        {
            return;
        }

        TraceLifetime(
            range,
            "first-cpu-write-disarm",
            range.FirstCpuWriteAddress,
            range.FirstCpuWritePage,
            range.FirstCpuWriteTraceSequence,
            range.FirstCpuWriteTimestampNanoseconds);
    }

    private static void TraceLifetime(
        TrackedRange range,
        string operation,
        ulong faultAddress = 0,
        ulong faultPage = 0,
        long traceSequence = 0,
        long timestampNanoseconds = 0)
    {
        if (traceSequence == 0)
        {
            traceSequence = Interlocked.Increment(ref _lifetimeTraceSequence);
        }

        if (timestampNanoseconds == 0)
        {
            timestampNanoseconds = GetMonotonicNanoseconds();
        }

        var elapsedMilliseconds =
            (timestampNanoseconds - _lifetimeTraceEpochNanoseconds) / 1_000_000.0;
        Console.Error.WriteLine(
            $"[WT][LIFETIME] seq={traceSequence} t_ms={elapsedMilliseconds:F3} " +
            $"event={operation} source_seq={range.SourceSequence} source='{range.Source}' " +
            $"requested=0x{range.Address:X16}+0x{range.ByteCount:X} " +
            $"range=0x{range.Start:X16}..0x{range.End:X16} " +
            $"fault=0x{faultAddress:X16} page=0x{faultPage:X16}");
    }

    private static long GetMonotonicNanoseconds()
    {
        Timespec time;
        return ClockGetTime(ClockMonotonicRaw, &time) == 0
            ? unchecked((time.Seconds * 1_000_000_000L) + time.Nanoseconds)
            : 0;
    }
}
