// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadMutexSemanticsTests
{
    [Fact]
    public void Grant_DropsOlderWaiterOwnedBySameRunningThread()
    {
        const BindingFlags PrivateStatic =
            BindingFlags.NonPublic | BindingFlags.Static;
        var exportsType = typeof(KernelPthreadCompatExports);
        var stateType = exportsType.GetNestedType(
            "PthreadMutexState",
            BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var state = Activator.CreateInstance(stateType);
        Assert.NotNull(state);

        var enqueue = exportsType.GetMethod(
            "EnqueueMutexWaiterLocked",
            PrivateStatic);
        var grant = exportsType.GetMethod(
            "TryGrantMutexWaiterLocked",
            PrivateStatic);
        Assert.NotNull(enqueue);
        Assert.NotNull(grant);

        const ulong threadId = 0x101;
        var staleWaiter = enqueue.Invoke(
            null,
            [state, threadId, true, null]);
        var currentWaiter = enqueue.Invoke(
            null,
            [state, threadId, true, null]);
        Assert.NotNull(staleWaiter);
        Assert.NotNull(currentWaiter);

        bool granted;
        lock (state)
        {
            granted = (bool)grant.Invoke(
                null,
                [state, currentWaiter])!;
        }
        Assert.True(granted);

        var owner = stateType.GetProperty("OwnerThreadId")!.GetValue(state);
        var waiters = stateType.GetProperty("Waiters")!.GetValue(state);
        var count = waiters!.GetType().GetProperty("Count")!.GetValue(waiters);
        Assert.Equal(threadId, owner);
        Assert.Equal(0, count);
    }
}
