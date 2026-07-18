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
    [InlineData(true, 4u, 0UL, true)]
    [InlineData(false, 4u, 0UL, false)]
    [InlineData(true, 8u, 0UL, false)]
    [InlineData(true, 4u, 1UL, false)]
    public void CompletionLabelClearDma_IsRecognizedNarrowly(
        bool compactLayout,
        uint byteCount,
        ulong sourceAddress,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.IsCompletionLabelClearDma(
                compactLayout,
                byteCount,
                sourceAddress));
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

    [Theory]
    [InlineData(false, 0x0000007021071EA0UL, 1UL, 0xFFFFFFFFUL, 3u, true)]
    [InlineData(true, 0x0000007021071EA0UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(false, 0x0000007021071EA1UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(false, 0x0000007021071EA0UL, 2UL, 0xFFFFFFFFUL, 3u, false)]
    public void RegisteredRecycledCompletionWait_IsRetiredNarrowly(
        bool is64Bit,
        ulong currentQword,
        ulong reference,
        ulong mask,
        uint compareFunction,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.ShouldRetireRegisteredGpuWait(
                is64Bit,
                currentQword,
                reference,
                mask,
                compareFunction));
    }

    [Theory]
    [InlineData(0x00000001E7010002UL, 1UL, 0xFFFFFFFFUL, 3u, true)]
    [InlineData(0x00000000E7010002UL, 1UL, 0xFFFFFFFFUL, 3u, true)]
    [InlineData(0x00000001E7010002UL, 2UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(0x00000002E7010002UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    [InlineData(0x00000001E7010003UL, 1UL, 0xFFFFFFFFUL, 3u, false)]
    public void RecycledRecordSignature_IsRecognizedNarrowly(
        ulong currentQword,
        ulong reference,
        ulong mask,
        uint compareFunction,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.IsRecycledGpuLabelStorage(
                currentQword,
                reference,
                mask,
                compareFunction));
    }

    [Theory]
    [InlineData(0x0000007021071EA0UL, 0UL, 4u, true)]
    [InlineData(0x0000007021071EA0UL, 1UL, 4u, true)]
    [InlineData(0x0000007021071EA0UL, 1UL, 8u, true)]
    [InlineData(0x00000001E7010002UL, 0UL, 4u, true)]
    [InlineData(0x00000001E7010002UL, 1UL, 4u, true)]
    [InlineData(0x00000000E7010002UL, 1UL, 4u, true)]
    [InlineData(0x0000007021071EA0UL, 2UL, 4u, false)]
    [InlineData(0x0000007021071EA0UL, 0UL, 8u, false)]
    [InlineData(0x0000007021071EA1UL, 0UL, 4u, false)]
    [InlineData(0x0000008021071EA0UL, 1UL, 4u, false)]
    [InlineData(0x0000000000000000UL, 1UL, 4u, false)]
    public void RecycledCompletionLabelWrite_IsSuppressedNarrowly(
        ulong currentQword,
        ulong value,
        uint byteCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.ShouldSuppressRecycledCompletionLabelWrite(
                currentQword,
                value,
                byteCount));
    }
}
