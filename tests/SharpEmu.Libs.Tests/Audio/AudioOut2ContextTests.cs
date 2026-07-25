// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ContextTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    // Silent Hill's AK::EventManager passes the two out-slots four bytes apart
    // with queued at the HIGHER address (rsi=...A94, rdx=...A90) and the frame's
    // stack canary immediately above them — mirror that exact layout.
    private const ulong AvailableAddress = MemoryBase + 0x100;
    private const ulong QueuedAddress = MemoryBase + 0x104;

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _ctx;

    public AudioOut2ContextTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ContextGetQueueLevel_WritesFourBytesToEachOutPointer()
    {
        // The ABI is (handle, uint32* queueLevel, uint32* queueAvailable) and
        // Silent Hill passes the two out-slots four bytes apart on its stack,
        // with the caller's canary right behind them. Poison the surrounding
        // bytes and require the export to touch exactly slot-sized regions.
        Span<byte> poison = stackalloc byte[16];
        poison.Fill(0xCD);
        Assert.True(_memory.TryWrite(AvailableAddress - 4, poison));

        // Handles come from a process-wide counter shared with every other test
        // in the assembly, so this case names one that can never be issued.
        _ctx[CpuRegister.Rdi] = ulong.MaxValue;
        _ctx[CpuRegister.Rsi] = QueuedAddress;
        _ctx[CpuRegister.Rdx] = AvailableAddress;
        var result = AudioOut2Exports.AudioOut2ContextGetQueueLevel(_ctx);

        Assert.Equal(0, result);

        Span<byte> after = stackalloc byte[16];
        Assert.True(_memory.TryRead(AvailableAddress - 4, after));
        // Byte before the lower slot untouched.
        Assert.Equal(0xCDCDCDCDu, BinaryPrimitives.ReadUInt32LittleEndian(after));
        // Both 4-byte slots written: unknown handle reports an empty queue with
        // full availability.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(after[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(after[8..]));
        // The bytes above the queued slot — where the guest's stack canary lives
        // in Silent Hill's AK::EventManager frame — must stay untouched. An
        // eight-byte write to the queued pointer lands exactly here.
        Assert.Equal(0xCDCDCDCDu, BinaryPrimitives.ReadUInt32LittleEndian(after[12..]));
    }

    [Fact]
    public void ContextGetQueueLevel_NullPointersAreAccepted()
    {
        _ctx[CpuRegister.Rdi] = ulong.MaxValue;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(_ctx));
    }

    [Fact]
    public void ContextGetQueueLevel_RisesWithPushesAndSumsToDepth()
    {
        // Wwise's sink-priming loop pushes grains until the reported queue
        // level rises; a level frozen at zero deadlocks audio init with the
        // bank-manager mutex held.
        const ulong paramAddress = MemoryBase + 0x300;
        const ulong outHandleAddress = MemoryBase + 0x380;
        _ctx[CpuRegister.Rdi] = paramAddress;
        _ctx[CpuRegister.Rsi] = MemoryBase + 0x1000;
        _ctx[CpuRegister.Rdx] = 0x10000;
        _ctx[CpuRegister.Rcx] = outHandleAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(_ctx));
        Span<byte> handleBytes = stackalloc byte[8];
        Assert.True(_memory.TryRead(outHandleAddress, handleBytes));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);

        _ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));
        _ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));

        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = QueuedAddress;
        _ctx[CpuRegister.Rdx] = AvailableAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(_ctx));

        Span<byte> levels = stackalloc byte[8];
        Assert.True(_memory.TryRead(AvailableAddress, levels));
        var available = BinaryPrimitives.ReadUInt32LittleEndian(levels);
        var queued = BinaryPrimitives.ReadUInt32LittleEndian(levels[4..]);
        Assert.True(queued >= 1, $"queue level should rise after pushes, was {queued}");
        Assert.Equal(2u, queued + available);
    }

    [Fact]
    public void ContextQueryMemory_WritesEightBytesOnly()
    {
        // Silent Hill places the eight-byte memory-info slot immediately below
        // the 0x40-byte context-param block it passes to ContextCreate next.
        // Anything written past the slot corrupts that param block in place.
        const ulong infoAddress = MemoryBase + 0x200;
        const ulong paramAddress = infoAddress + 8;
        Span<byte> poison = stackalloc byte[0x18];
        poison.Fill(0xAB);
        Assert.True(_memory.TryWrite(infoAddress, poison));

        _ctx[CpuRegister.Rdi] = paramAddress;
        _ctx[CpuRegister.Rsi] = infoAddress;
        var result = AudioOut2Exports.AudioOut2ContextQueryMemory(_ctx);

        Assert.Equal(0, result);
        Span<byte> after = stackalloc byte[0x18];
        Assert.True(_memory.TryRead(infoAddress, after));
        Assert.NotEqual(0xABABABABABABABABUL, BinaryPrimitives.ReadUInt64LittleEndian(after));
        // The param block starting eight bytes in must be untouched.
        Assert.Equal(0xABABABABABABABABUL, BinaryPrimitives.ReadUInt64LittleEndian(after[8..]));
        Assert.Equal(0xABABABABABABABABUL, BinaryPrimitives.ReadUInt64LittleEndian(after[16..]));
    }
}
