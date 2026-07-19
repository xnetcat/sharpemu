// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// Exercises the CPU-write invalidation contract the stale-texture fix relies
/// on: a guest image whose backing memory the tracker arms must report dirty
/// after a managed guest write, clear on consume, and observe further writes
/// once re-armed. The presenter's fast paths only re-read guest memory when
/// this contract fires, so a break here is exactly the "UI atlas sampling
/// stale content" bug. These run only where the tracker is active (POSIX);
/// on platforms where it is a no-op the calls degrade to false and the asserts
/// below are skipped.
/// </summary>
public sealed unsafe class GuestImageWriteTrackerTests
{
    private const int PageSize = 4096;

    [Fact]
    public void TrackedManagedWrite_MarksRangeDirtyUntilConsumed()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        // Span several pages to mimic an oversized (>1080p) surface: the fix
        // removed the size cap that previously left such ranges untracked.
        var byteCount = (ulong)(PageSize * 3);
        var backing = NativeMemory.AlignedAlloc((nuint)byteCount, PageSize);
        var address = (ulong)backing;
        try
        {
            GuestImageWriteTracker.Track(address, byteCount, source: "test.oversized-image");
            Assert.False(GuestImageWriteTracker.PeekDirty(address));

            // A managed HLE write (DMA apply / decode output) into the range
            // must dirty it, matching what PhysicalVirtualMemory.TryWrite does.
            Assert.True(
                GuestImageWriteTracker.PrepareWrite(address + PageSize, 16));

            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            // Consuming clears the flag so a re-upload happens exactly once.
            Assert.False(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.AlignedFree(backing);
        }
    }

    [Fact]
    public void RearmedRange_ObservesSubsequentWrite()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var byteCount = (ulong)PageSize;
        var backing = NativeMemory.AlignedAlloc((nuint)byteCount, PageSize);
        var address = (ulong)backing;
        try
        {
            GuestImageWriteTracker.Track(address, byteCount, source: "test.rearm");

            Assert.True(GuestImageWriteTracker.PrepareWrite(address, 8));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));

            // Re-arm after re-upload (the per-flip lifecycle). A later guest
            // store must be seen again; without re-arm the surface would freeze.
            GuestImageWriteTracker.Rearm(address);
            Assert.False(GuestImageWriteTracker.PeekDirty(address));

            Assert.True(GuestImageWriteTracker.PrepareWrite(address, 8));
            Assert.True(GuestImageWriteTracker.PeekDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.AlignedFree(backing);
        }
    }

    [Fact]
    public void UntrackedAddress_IsNeverDirty()
    {
        // The promotion path previously never armed tracking, so its address
        // was permanently "clean" and the fast path served stale texels. This
        // documents that arming is required for any invalidation to occur.
        var byteCount = (ulong)PageSize;
        var backing = NativeMemory.AlignedAlloc((nuint)byteCount, PageSize);
        var address = (ulong)backing;
        try
        {
            Assert.False(GuestImageWriteTracker.PeekDirty(address));
            Assert.False(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            NativeMemory.AlignedFree(backing);
        }
    }
}
