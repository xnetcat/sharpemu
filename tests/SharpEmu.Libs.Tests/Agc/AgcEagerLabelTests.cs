// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEagerLabelTests
{
    [Fact]
    public void OrdinaryGuestWrite_IsAppliedEagerlyWithoutWaiter()
    {
        Assert.True(AgcExports.ShouldEagerlyApplyGuestWrite(
            hasActiveWait: false,
            eagerWatchedLabelWrite: false));
    }

    [Fact]
    public void GenericWrite_DoesNotBypassActiveWait()
    {
        Assert.False(AgcExports.ShouldEagerlyApplyGuestWrite(
            hasActiveWait: true,
            eagerWatchedLabelWrite: false));
    }

    [Fact]
    public void ReleaseLabelWrite_FeedsActiveWaitBeforeGuestCanRecycleLabel()
    {
        Assert.True(AgcExports.ShouldEagerlyApplyGuestWrite(
            hasActiveWait: true,
            eagerWatchedLabelWrite: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void QueuedWrite_RetriesOnlyWhenEagerWriteWasNotAttempted(
        bool eagerAttempted,
        bool expectedRetry)
    {
        Assert.Equal(
            expectedRetry,
            AgcExports.ShouldRetryQueuedGuestWrite(eagerAttempted));
    }

    [Theory]
    [InlineData(0x0000007021071EA0UL, 1UL, 0xFFFFFFFFUL, 3u, true)]
    [InlineData(0x0000007021071EA1UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(0x0000008021071EA0UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(0x0000007021071EA0UL, 2UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(0x0000007021071EA0UL, 1UL, 0x0000FFFFUL, 3u, false)]
    [InlineData(0x0000007021071EA0UL, 1UL, 0xFFFFFFFFUL, 4u, false)]
    public void RecycledCompletionLabel_IsRecognizedNarrowly(
        ulong currentQword,
        ulong reference,
        ulong mask,
        uint compareFunction,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.IsRecycledGpuLabelPointer(
                currentQword,
                reference,
                mask,
                compareFunction));
    }
}
