// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadStagedStateTests
{
    [Fact]
    public void NestedExecution_CannotConsumeInterruptedImportActions()
    {
        var waiter = new TestWaiter();
        var transfer = new GuestCpuContinuation
        {
            Rip = 0x1234_5678,
            Rsp = 0x2345_6780,
        };
        var previousThread = GuestThreadExecution.EnterGuestThread(0x42);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x3456_7890,
            resumeRsp: 0x4567_89A0,
            returnSlotAddress: 0x4567_8998);
        try
        {
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(
                context: null,
                reason: "outer",
                wakeKey: "outer-wake",
                waiter: waiter,
                blockDeadlineTimestamp: 123));
            GuestThreadExecution.RequestCurrentEntryExit("outer-exit", 77UL);
            GuestThreadExecution.RequestCurrentContextTransfer(transfer);

            var outer = GuestThreadExecution.SaveAndResetStagedState();

            Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
            Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out _));
            Assert.False(GuestThreadExecution.TryConsumeCurrentContextTransfer(out _));
            Assert.True(GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame));
            Assert.Equal(0x3456_7890UL, frame.ReturnRip);

            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock("nested"));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out var nestedReason));
            Assert.Equal("nested", nestedReason);

            GuestThreadExecution.RestoreStagedState(outer);

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out var restoredWaiter,
                out var deadline));
            Assert.Equal("outer", reason);
            Assert.False(hasContinuation);
            Assert.Equal("outer-wake", wakeKey);
            Assert.Same(waiter, restoredWaiter);
            Assert.Equal(123, deadline);
            Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(
                out var exitValue,
                out var exitReason));
            Assert.Equal(77UL, exitValue);
            Assert.Equal("outer-exit", exitReason);
            Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(
                out var restoredTransfer));
            Assert.Equal(transfer, restoredTransfer);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private sealed class TestWaiter : IGuestThreadBlockWaiter
    {
        public int Resume() => 0;

        public bool TryWake() => false;
    }
}
