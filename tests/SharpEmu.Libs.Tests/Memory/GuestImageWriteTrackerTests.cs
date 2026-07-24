// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// The write generation lets GPU caches detect guest CPU rewrites even after
/// another cache owner consumed the (single) dirty flag: the generation is
/// monotonic and survives consume/re-arm cycles and range replacement. These
/// invariants back the presenter's stale-upload detection for CPU-rewritten
/// images (video planes, streamed font atlases).
/// </summary>
public sealed unsafe class GuestImageWriteTrackerTests
{
    // The tracker aligns to the guest's 4 KiB pages; the mprotect underneath
    // operates on host pages, which are 16 KiB on Apple Silicon (the emulator
    // itself always runs with 4 KiB host pages under Rosetta, but this test
    // host may not). Align the allocation to the largest host page size so
    // the kernel's rounding stays inside memory this test owns instead of
    // spilling onto neighbouring heap pages.
    private const nuint TrackedByteCount = 4096;
    private const nuint HostPageAlignment = 16384;

    private static ulong AllocateTrackedPages(out void* allocation)
    {
        allocation = NativeMemory.AlignedAlloc(2 * HostPageAlignment, HostPageAlignment);
        return (ulong)allocation;
    }

    [Fact]
    public void GenerationSurvivesDirtyConsume()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(0, generation);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));

            // Consuming the dirty flag must not roll back the generation:
            // that is exactly what lets a second cache owner still observe
            // the rewrite after the first owner consumed the flag.
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.Free(allocation);
        }
    }

    [Fact]
    public void GenerationIncrementsOncePerArmedLifetime()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            // The first fault disarmed the range; later writes are free-running
            // and must not inflate the generation until the owner re-arms.
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);

            GuestImageWriteTracker.Rearm(address);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(2, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.Free(allocation);
        }
    }

    [Fact]
    public void GenerationCarriesAcrossRangeReplacement()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            // Re-registering the same allocation with a different size retires
            // the range object (the signal handler may still see the old
            // snapshot) but must carry the generation, otherwise a resize
            // would hide the rewrite from cache owners.
            GuestImageWriteTracker.Track(address, 2 * TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.Free(allocation);
        }
    }

    [Fact]
    public void UntrackedAddressHasNoGeneration()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Assert.False(GuestImageWriteTracker.TryGetWriteGeneration(0xDEAD_0000_0000UL, out _));
    }

    // ---- managed-write lease ------------------------------------------------
    //
    // A managed guest write copies with Buffer.MemoryCopy. If the tracker
    // mprotects the destination read-only at any instant during that copy the
    // runtime raises a fatal, non-resumable AccessViolationException (native
    // guest stores recover through the POSIX signal bridge; managed ones cannot).
    // So the contract these tests pin is not "the arm is eventually undone" but
    // "the arm cannot happen at all while a managed write is in flight".

    /// <summary>
    /// Probes whether a host page is really writable without writing to it from
    /// managed code. read(2) copies into the target buffer inside the kernel and
    /// reports EFAULT for a read-only destination, so an unwritable page can be
    /// observed as a return value instead of as the fatal AccessViolation a
    /// managed store would raise.
    /// </summary>
    private sealed unsafe class HostWritabilityProbe : IDisposable
    {
        private const int EFault = 14;

        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
        private static extern int Pipe(int* fileDescriptors);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern nint Read(int fileDescriptor, void* buffer, nuint count);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern nint Write(int fileDescriptor, void* buffer, nuint count);

        [DllImport("libc", EntryPoint = "close")]
        private static extern int Close(int fileDescriptor);

        private readonly int _readEnd;
        private readonly int _writeEnd;

        public HostWritabilityProbe()
        {
            var fileDescriptors = stackalloc int[2];
            if (Pipe(fileDescriptors) != 0)
            {
                throw new InvalidOperationException("pipe(2) failed");
            }

            _readEnd = fileDescriptors[0];
            _writeEnd = fileDescriptors[1];
        }

        public bool IsWritable(ulong address)
        {
            byte payload = 0;
            if (Write(_writeEnd, &payload, 1) != 1)
            {
                throw new InvalidOperationException("probe write(2) failed");
            }

            // Reading a tracked page is always permitted (the tracker only ever
            // drops write access), so this cannot fault.
            var original = *(byte*)address;
            if (Read(_readEnd, (void*)address, 1) == 1)
            {
                *(byte*)address = original;
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            // Drain the byte the failed read left queued so the next probe
            // starts from a known state.
            _ = Read(_readEnd, &payload, 1);
            if (error != EFault)
            {
                // Anything other than EFAULT says nothing about page
                // protection; do not report a false violation.
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            Close(_readEnd);
            Close(_writeEnd);
        }
    }

    // 1. A re-arm that lands while a managed guest write is copying is the AV.
    //    Deferring the arm is not enough: the deferral decision is taken before
    //    the mprotect, so an arm that misses the pin still protects the page and
    //    only retracts it afterwards - far too late for a copy already running.
    //    Rearm must instead wait for the lease.
    [Fact]
    public void RearmWaitsForAnInFlightManagedWrite()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        var leaseHeld = false;
        Thread? rearm = null;
        var rearmReturned = new ManualResetEventSlim(false);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            leaseHeld = GuestImageWriteTracker.BeginManagedWrite(address, TrackedByteCount);
            Assert.True(leaseHeld);

            rearm = new Thread(() =>
            {
                GuestImageWriteTracker.Rearm(address);
                rearmReturned.Set();
            })
            {
                IsBackground = true,
            };
            rearm.Start();

            // The lease is held: the arm must not complete (and so must not
            // have mprotected the page) while the copy is in flight.
            Assert.False(rearmReturned.Wait(TimeSpan.FromMilliseconds(250)));

            leaseHeld = false;
            GuestImageWriteTracker.EndManagedWrite();

            // Draining the last lease must let the arm through promptly,
            // otherwise the tracker would stop noticing guest CPU writes.
            Assert.True(rearmReturned.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // A leaked lease would wedge every later arm process-wide, so it
            // must be released even when an assertion above fails.
            if (leaseHeld)
            {
                GuestImageWriteTracker.EndManagedWrite();
            }

            rearm?.Join(TimeSpan.FromSeconds(10));
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.Free(allocation);
        }
    }

    // 2. The invariant the AV violates, stated directly: for as long as
    //    BeginManagedWrite reports a lease, every page in the write span stays
    //    host-writable no matter how hard a GPU thread re-arms the range.
    [Fact]
    public void ConcurrentRearmNeverProtectsPagesUnderAManagedWrite()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        using var probe = new HostWritabilityProbe();
        var stop = false;
        Thread? armer = null;
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);

            armer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    GuestImageWriteTracker.Rearm(address);
                }
            })
            {
                IsBackground = true,
            };
            armer.Start();

            var violations = new List<string>();
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                if (!GuestImageWriteTracker.BeginManagedWrite(
                        address,
                        TrackedByteCount,
                        out var pagesWritable))
                {
                    continue;
                }

                try
                {
                    // A real copy is not instantaneous. Probe repeatedly so the
                    // lease actually spans a window an arm could land inside.
                    for (var probeIndex = 0; probeIndex < 16; probeIndex++)
                    {
                        if (probe.IsWritable(address))
                        {
                            continue;
                        }

                        var recovered = probe.IsWritable(address);
                        violations.Add(
                            $"iteration={iteration} probe={probeIndex} " +
                            $"reported_writable={pagesWritable} recovered={recovered}");
                        break;
                    }
                }
                finally
                {
                    GuestImageWriteTracker.EndManagedWrite();
                }
            }

            Assert.True(violations.Count == 0, string.Join("; ", violations.Take(8)));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            armer?.Join(TimeSpan.FromSeconds(10));
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.Free(allocation);
        }
    }

    // 3. End-to-end over the path the Silent Hill crash takes: AgcExports'
    //    release-label write reaches guest memory through
    //    CpuContext.TryWriteUInt64 -> TrackedCpuMemory -> PhysicalVirtualMemory.
    //    A label write that silently fails strands every WAIT_REG_MEM waiting on
    //    it, so "returned false" is as damaging as the crash: with the range
    //    being re-armed concurrently, every write must still land.
    [Fact]
    public void GuestWriteReachesMemoryWhileTheRangeIsRearmedConcurrently()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var backing = NativeMemory.AlignedAlloc(4 * HostPageAlignment, HostPageAlignment);
        var stop = false;
        Thread? armer = null;
        ulong trackedAddress = 0;
        try
        {
            using var host = new RealBackedHostMemory((ulong)backing);
            using var memory = new PhysicalVirtualMemory(host);
            var region = memory.AllocateAt(0, 2 * HostPageAlignment, executable: false);
            Assert.NotEqual(0UL, region);

            trackedAddress = region;
            GuestImageWriteTracker.Track(trackedAddress, TrackedByteCount);

            armer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    GuestImageWriteTracker.Rearm(trackedAddress);
                }
            })
            {
                IsBackground = true,
            };
            armer.Start();

            var labelAddress = region + 0x40;
            Span<byte> readBack = stackalloc byte[sizeof(ulong)];
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var value = 0xA5A5_0000UL + (ulong)iteration;
                var label = BitConverter.GetBytes(value);
                Assert.True(memory.TryWrite(labelAddress, label));
                Assert.True(memory.TryRead(labelAddress, readBack));
                Assert.Equal(value, BitConverter.ToUInt64(readBack));
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            armer?.Join(TimeSpan.FromSeconds(10));
            if (trackedAddress != 0)
            {
                GuestImageWriteTracker.Untrack(trackedAddress);
            }

            NativeMemory.AlignedFree(backing);
        }
    }

    // Identity-mapped host memory backed by a real allocation the test owns, so
    // PhysicalVirtualMemory's copies land in memory the tracker can mprotect.
    private sealed class RealBackedHostMemory(ulong address) : IHostMemory, IDisposable
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => address;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => address;

        public bool Commit(ulong commitAddress, ulong size, HostPageProtection protection) => true;

        // The allocation is owned by the test body, not by the memory object.
        public bool Free(ulong freeAddress) => true;

        public bool Protect(
            ulong protectAddress,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(
            ulong protectAddress,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong queryAddress, out HostRegionInfo info)
        {
            var pageAddress = queryAddress & ~0xFFFUL;
            info = new HostRegionInfo(
                pageAddress,
                pageAddress,
                0x1000,
                HostRegionState.Committed,
                0,
                HostPageProtection.ReadWrite,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong flushAddress, ulong size)
        {
        }

        public void Dispose()
        {
        }
    }
}
