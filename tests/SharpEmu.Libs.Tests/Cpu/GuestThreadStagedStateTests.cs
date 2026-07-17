// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadStagedStateTests
{
    [Fact]
    public void SaveAndRestore_IsolatesAllImportBoundaryActions()
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        var waiter = new TestWaiter();
        var outerTransfer = default(GuestCpuContinuation) with
        {
            Rip = 0x8000_1234,
            Rsp = 0x7000_5678,
        };

        try
        {
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(
                context: null,
                reason: "outer wait",
                wakeKey: "outer-key",
                waiter,
                blockDeadlineTimestamp: 123456));
            GuestThreadExecution.RequestCurrentEntryExit("outer exit", 0xFEDC_BA98UL);
            GuestThreadExecution.RequestCurrentContextTransfer(outerTransfer);

            var saved = GuestThreadExecution.SaveAndResetStagedState();

            Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
            Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out _));
            Assert.False(GuestThreadExecution.TryConsumeCurrentContextTransfer(out _));

            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock("nested wait"));
            GuestThreadExecution.RestoreStagedState(saved);

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out var restoredWaiter,
                out var deadline));
            Assert.Equal("outer wait", reason);
            Assert.False(hasContinuation);
            Assert.Equal("outer-key", wakeKey);
            Assert.Same(waiter, restoredWaiter);
            Assert.Equal(123456, deadline);

            Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(
                out var exitValue,
                out var exitReason));
            Assert.Equal(0xFEDC_BA98UL, exitValue);
            Assert.Equal("outer exit", exitReason);

            Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(
                out var restoredTransfer));
            Assert.Equal(outerTransfer, restoredTransfer);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private sealed class TestWaiter : IGuestThreadBlockWaiter
    {
        public int Resume() => 0;

        public bool TryWake() => false;
    }
}
