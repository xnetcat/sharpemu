// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// sceVideoOutAddOutputModeEvent is part of the Gen5 display handshake: the title registers for
// output-mode changes at boot and waits for the current mode to be reported back before it
// finishes bringing up its display pipeline. Unresolved, the guest deletes the equeue and gives
// up. Kind 2 is the output-mode event as reported by sceVideoOutGetEventId (0 = flip,
// 1 = vblank).
public sealed class VideoOutOutputModeEventTests
{
    private const string OpenNid = "Up36PTk687E";
    private const string CloseNid = "uquVH4-Du78";
    private const string AddOutputModeEventNid = "kmSe30JTs+E";

    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong HandleOutAddress = MemoryBase + 0x100;
    private const ulong EventsAddress = MemoryBase + 0x200;
    private const ulong OutCountAddress = MemoryBase + 0x300;
    private const ulong TimeoutAddress = MemoryBase + 0x400;
    private const ulong EventDataAddress = MemoryBase + 0x500;

    private const short OrbisKernelEventFilterVideoOut = -13;
    private static readonly ulong InvalidHandle = unchecked((ulong)(int)0x8029000B);
    private static readonly ulong InvalidEventQueue = unchecked((ulong)(int)0x8029000C);

    [Fact]
    public void AddOutputModeEvent_IsGen5OnlyExport()
    {
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport(AddOutputModeEventNid, out _));

        var manager = CreateGen5Manager();
        Assert.True(manager.TryGetExport(AddOutputModeEventNid, out var export));
        Assert.Equal("sceVideoOutAddOutputModeEvent", export.Name);
        Assert.Equal("libSceVideoOut", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void AddOutputModeEvent_DeliversReadableOutputModeEvent()
    {
        const ulong userData = 0x1357_9BDF_2468_ACE0;

        var manager = CreateGen5Manager();
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);
        var handle = OpenPort(manager, ctx);

        try
        {
            ctx[CpuRegister.Rdi] = equeue;
            ctx[CpuRegister.Rsi] = handle;
            ctx[CpuRegister.Rdx] = userData;
            Assert.True(manager.TryDispatch(AddOutputModeEventNid, ctx, out _));
            Assert.Equal((ulong)(int)OrbisGen2Result.ORBIS_GEN2_OK, ctx[CpuRegister.Rax]);

            // The registration itself reports the current output mode so the guest's
            // boot handshake completes without waiting for a real mode change.
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
            Assert.Equal(
                OrbisKernelEventFilterVideoOut,
                ReadInt16(memory, EventsAddress + 0x08));
            Assert.Equal(userData, ReadUInt64(memory, EventsAddress + 0x18));

            ctx[CpuRegister.Rdi] = EventsAddress;
            Assert.Equal(2, VideoOutExports.VideoOutGetEventId(ctx));

            ctx[CpuRegister.Rdi] = EventsAddress;
            ctx[CpuRegister.Rsi] = EventDataAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutGetEventData(ctx));
            // TriggerDisplayEvent packs a repeat count and timestamp into the low 16
            // bits; the decoded payload for a mode report with no hint is zero.
            Assert.Equal(0UL, ReadUInt64(memory, EventDataAddress));
        }
        finally
        {
            ClosePort(manager, ctx, handle);
            DeleteEqueue(ctx, equeue);
        }
    }

    [Fact]
    public void AddOutputModeEvent_ValidatesHandleAndEventQueue()
    {
        var manager = CreateGen5Manager();
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);
        var handle = OpenPort(manager, ctx);

        try
        {
            ctx[CpuRegister.Rdi] = equeue;
            ctx[CpuRegister.Rsi] = ulong.MaxValue;
            ctx[CpuRegister.Rdx] = 0;
            Assert.True(manager.TryDispatch(AddOutputModeEventNid, ctx, out _));
            Assert.Equal(InvalidHandle, ctx[CpuRegister.Rax]);

            ctx[CpuRegister.Rdi] = 0xDEAD_BEEF;
            ctx[CpuRegister.Rsi] = handle;
            ctx[CpuRegister.Rdx] = 0;
            Assert.True(manager.TryDispatch(AddOutputModeEventNid, ctx, out _));
            Assert.Equal(InvalidEventQueue, ctx[CpuRegister.Rax]);
        }
        finally
        {
            ClosePort(manager, ctx, handle);
            DeleteEqueue(ctx, equeue);
        }
    }

    private static ModuleManager CreateGen5Manager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static ulong OpenPort(ModuleManager manager, CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch(OpenNid, ctx, out _));
        var handle = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static void ClosePort(ModuleManager manager, CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        _ = manager.TryDispatch(CloseNid, ctx, out _);
    }

    private static ulong CreateEqueue(CpuContext ctx, FakeCpuMemory memory)
    {
        ctx[CpuRegister.Rdi] = HandleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        return ReadUInt64(memory, HandleOutAddress);
    }

    private static void DeleteEqueue(CpuContext ctx, ulong equeue)
    {
        ctx[CpuRegister.Rdi] = equeue;
        _ = KernelEventQueueCompatExports.KernelDeleteEqueue(ctx);
    }

    private static int WaitEqueue(CpuContext ctx, FakeCpuMemory memory, ulong equeue)
    {
        WriteUInt64(memory, TimeoutAddress, 0);
        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = EventsAddress;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = OutCountAddress;
        ctx[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static short ReadInt16(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[2];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadInt16LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
