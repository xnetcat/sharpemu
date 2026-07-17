// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// PhysicalVirtualMemory is the host-backed (identity-mapped) implementation.
// Reserve-only regions (> 4 GiB, non-executable) defer commit until first
// access; TryAllocateGuestMemory serves a first-fit free-list with coalescing.
// These tests pin that behaviour through fake IHostMemory implementations.
public sealed class PhysicalVirtualMemoryTests
{
    // 1. Lazy commit: a reserve-only region has its pages committed on demand
    //    when read; freshly committed pages read as zero.
    [Fact]
    public void LazyReadCommitsPageOnDemandAndReadsZero()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        // > 4 GiB, non-executable -> reserve-only with lazy commit.
        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.NotEqual(0UL, address);

        // Discard the priming commits AllocateAt issues up front; we want to
        // observe the on-demand commit triggered by the read itself.
        host.ResetCommitState();

        var readAddress = address;
        var buffer = new byte[1];
        Assert.True(memory.TryRead(readAddress, buffer));
        Assert.Equal(0, buffer[0]);

        // The touched page was committed on demand.
        var page = readAddress & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    // 2. Reserve-only region: GetPointer commits the page before returning it,
    //    so callers receive a valid (non-null) pointer. An unmapped address yields null.
    [Fact]
    public unsafe void GetPointerOnReserveOnlyRegionCommitsAndReturnsValidPointer()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.ResetCommitState();

        var pointerAddress = address + 0x123;
        var pointer = memory.GetPointer(pointerAddress);
        Assert.NotEqual(0UL, (ulong)pointer);
        Assert.Equal(pointerAddress, (ulong)pointer);

        var page = pointerAddress & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    [Fact]
    public unsafe void GetPointerOnUnmappedAddressReturnsNull()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        Assert.Equal(0UL, (ulong)memory.GetPointer(0x0001_0000));
    }

    [Fact]
    public void DynamicProtectionIsObservedByManagedWrites()
    {
        using var host = new ProtectionTrackingHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.True(memory.TryProtect(address, 0x1000, GuestPageProtection.Read));
        host.ProtectCalls.Clear();

        Assert.True(memory.TryWrite(address, [0x5A]));
        Assert.Contains(
            host.ProtectCalls,
            call => call.Address == address &&
                    call.Size == 1 &&
                    call.Protection == HostPageProtection.ReadWriteExecute);
    }

    // 3. Free-list reuse: a freed range is served back by first-fit allocation,
    //    preferring the lowest fitting free range over the larger trailing span.
    [Fact]
    public void FreedRangeIsReusedByFirstFitAllocation()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var second));
        Assert.NotEqual(first, second);
        Assert.True(memory.TryFreeGuestMemory(first));

        // A smaller allocation must reuse first's freed slot (lowest fitting range),
        // not the larger trailing free range.
        Assert.True(memory.TryAllocateGuestMemory(0x2000, 0x1000, out var reused));
        Assert.Equal(first, reused);
    }

    // 4. Coalescing: freeing the middle of three adjacent ranges merges both the
    //    left and right free neighbours in a single TryFreeGuestMemory call,
    //    restoring the full span for subsequent first-fit reuse.
    [Fact]
    public void FreeingMiddleRangeCoalescesBothNeighbours()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        // Three adjacent 0x1000 allocations: offsets 0x1000, 0x2000, 0x3000.
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var second));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var third));

        // Free the outer ranges first, leaving two separate free ranges.
        Assert.True(memory.TryFreeGuestMemory(first));
        Assert.True(memory.TryFreeGuestMemory(third));

        // Freeing the middle range must coalesce both neighbours at once.
        Assert.True(memory.TryFreeGuestMemory(second));

        // The whole arena is now one coalesced free range; a full-arena allocation
        // reuses first's base address.
        Assert.True(memory.TryAllocateGuestMemory(0x000F_F000, 0x1000, out var coalesced));
        Assert.Equal(first, coalesced);
    }

    /// <summary>
    /// Host memory backed by a single real, zero-initialised page. Reserve/Allocate
    /// report the page-aligned buffer address so lazy-commit read paths can actually
    /// dereference the returned pointer. Query always reports Reserved, so
    /// EnsureRangeCommitted issues a Commit on first access.
    /// </summary>
    private sealed unsafe class LazyZeroedHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _address;
        private readonly Dictionary<ulong, HostPageProtection> _committedPages = [];
        private bool _freed;

        public LazyZeroedHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x2000);
            _address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];
        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectCalls { get; } = [];

        public void ResetCommitState()
        {
            CommitCalls.Clear();
            _committedPages.Clear();
        }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            CommitCalls.Add((address, size, protection));
            var end = address + size;
            for (var page = address & ~0xFFFUL; page < end; page += 0x1000)
            {
                _committedPages[page] = protection;
            }
            return true;
        }

        public bool Free(ulong address)
        {
            // The real buffer is released in Dispose; keep Free a no-op so
            // PhysicalVirtualMemory.Clear does not double-free it.
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            ProtectCalls.Add((address, size, protection));
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            var pageAddress = address & ~0xFFFUL;
            var committed = _committedPages.TryGetValue(pageAddress, out var protection);
            info = new HostRegionInfo(
                pageAddress,
                pageAddress,
                0x1000,
                committed ? HostRegionState.Committed : HostRegionState.Reserved,
                0,
                committed ? protection : HostPageProtection.NoAccess,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.NativeMemory.Free(_allocation);
                _freed = true;
            }
        }
    }

    private sealed unsafe class ProtectionTrackingHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _address;
        private HostPageProtection _protection;
        private bool _freed;

        public ProtectionTrackingHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(0x1000, 0x1000);
            _address = (ulong)_allocation;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            _protection = protection;
            return _address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            _protection = protection;
            return true;
        }

        public bool Free(ulong address) => true;

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            ProtectCalls.Add((address, size, protection));
            rawOldProtection = ToRawProtection(_protection);
            _protection = protection;
            return true;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = ToRawProtection(_protection);
            _protection = FromRawProtection(rawProtection);
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = new HostRegionInfo(
                _address,
                _address,
                0x1000,
                HostRegionState.Committed,
                0,
                _protection,
                ToRawProtection(_protection),
                ToRawProtection(_protection));
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.NativeMemory.AlignedFree(_allocation);
                _freed = true;
            }
        }

        private static uint ToRawProtection(HostPageProtection protection) => protection switch
        {
            HostPageProtection.ReadOnly => 0x02,
            HostPageProtection.ReadWrite => 0x04,
            HostPageProtection.Execute => 0x10,
            HostPageProtection.ReadExecute => 0x20,
            HostPageProtection.ReadWriteExecute => 0x40,
            HostPageProtection.ExecuteWriteCopy => 0x80,
            _ => 0x01,
        };

        private static HostPageProtection FromRawProtection(uint protection) => protection switch
        {
            0x02 => HostPageProtection.ReadOnly,
            0x04 => HostPageProtection.ReadWrite,
            0x10 => HostPageProtection.Execute,
            0x20 => HostPageProtection.ReadExecute,
            0x40 => HostPageProtection.ReadWriteExecute,
            0x80 => HostPageProtection.ExecuteWriteCopy,
            _ => HostPageProtection.NoAccess,
        };
    }

    // Minimal host memory for free-list tests: Allocate honours the desired
    // address (or a fallback), everything else succeeds as a no-op. The guest
    // allocation arena never dereferences, so no real backing is required.
    private sealed class FakeHostMemory : IHostMemory
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            desiredAddress != 0 ? desiredAddress : 0x00007000_0000_0000;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }
}
