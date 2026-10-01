// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class UncontendedMutexDispatchTests
{
    [Fact]
    public void UninitializedMutexFallsBackAndInitializedMutexUsesNormalOwnership()
    {
        var context = Context(0x6_1000_0000);
        var address = context[CpuRegister.Rdi];
        Assert.False(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.True(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
        Assert.False(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
    }

    [Fact]
    public void OtherOwnerFallsBackWithoutTakingOrReleasingOwnership()
    {
        var context = Context(0x6_1001_0000);
        var address = context[CpuRegister.Rdi];
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        var previous = GuestThreadExecution.EnterGuestThread(0x610001);
        try
        {
            Assert.True(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
            GuestThreadExecution.EnterGuestThread(0x610002);
            Assert.False(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, KernelPthreadCompatExports.PthreadMutexTrylock(context));
            GuestThreadExecution.EnterGuestThread(0x610001);
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
            GuestThreadExecution.EnterGuestThread(0x610002);
            Assert.True(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
        }
        finally { GuestThreadExecution.RestoreGuestThread(previous); }
    }

    [Fact]
    public void ReusedSlotAcquiresTheNewMutex()
    {
        var context = Context(0x6_1002_0000);
        var slot = context[CpuRegister.Rdi];
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.True(context.TryReadUInt64(slot, out var oldHandle));
        context[CpuRegister.Rdi] = slot + 0x100;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.True(context.TryReadUInt64(slot + 0x100, out var newHandle));
        Assert.True(context.TryWriteUInt64(slot, newHandle));
        Assert.True(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, slot));
        context[CpuRegister.Rdi] = newHandle;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, KernelPthreadCompatExports.PthreadMutexTrylock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        context[CpuRegister.Rdi] = oldHandle;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
        context[CpuRegister.Rdi] = newHandle;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
    }

    [Fact]
    public void FreeMutexWithQueuedWaiterDoesNotAllowBarging()
    {
        var context = Context(0x6_1003_0000);
        var address = context[CpuRegister.Rdi];
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        var type = typeof(KernelPthreadCompatExports);
        var states = (IDictionary)type.GetField("_mutexStates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var state = states[address]!;
        var waiter = type.GetMethod("EnqueueMutexWaiterLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, 0x610003UL, true, "uncontended-test"]);
        Assert.False(KernelPthreadCompatExports.TryLockInitializedMutexUncontended(context, address));
        Assert.Equal(0UL, state.GetType().GetProperty("OwnerThreadId")!.GetValue(state));
        Assert.True((bool)type.GetMethod("TryGrantMutexWaiterLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, waiter])!);
        var previous = GuestThreadExecution.EnterGuestThread(0x610003);
        try
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
        }
        finally { GuestThreadExecution.RestoreGuestThread(previous); }
    }

    private static CpuContext Context(ulong address)
    {
        var context = new CpuContext(new PthreadMutexSemanticsTests.AllocatingCpuMemory(address, 0x4000), Generation.Gen5);
        context[CpuRegister.Rdi] = address + 0x100;
        return context;
    }
}
