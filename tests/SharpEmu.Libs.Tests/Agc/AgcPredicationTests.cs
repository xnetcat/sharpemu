// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPredicationTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CommandAddress = BaseAddress + 0x400;
    private const ulong CommandEndAddress = BaseAddress + 0x800;

    [Fact]
    public void DcbSetPredicationUsesFiveArgumentAbi()
    {
        var (memory, ctx) = CreateCommandContext();
        const ulong conditionAddress = 0x70_1234_5678;
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = conditionAddress;

        Assert.Equal(0, AgcExports.DcbSetPredication(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_2000u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x0003_1100u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(unchecked((uint)conditionAddress) & ~0xFu, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal((uint)(conditionAddress >> 32), ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(CommandAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbJumpUsesTargetAndSizeRegistersAndEncodesControl()
    {
        var (memory, ctx) = CreateCommandContext();
        const ulong targetAddress = 0x70_2345_678B;
        const uint targetDwords = 0x34567;
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 2;
        ctx[CpuRegister.Rcx] = targetAddress;
        ctx[CpuRegister.R8] = targetDwords;

        Assert.Equal(0, AgcExports.DcbJump(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_3F00u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(unchecked((uint)targetAddress) & ~0x3u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal((uint)(targetAddress >> 32), ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(
            0x0F20_0000u | (2u << 28) | (1u << 20) | targetDwords,
            ReadUInt32(memory, CommandAddress + 12));
    }

    [Fact]
    public void SetPacketPredicationUpdatesTypeThreePredicateBit()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, 0xC002_3F00u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 1;

        Assert.Equal(0, AgcExports.SetPacketPredication(ctx));
        Assert.Equal(0xC002_3F01u, ReadUInt32(memory, CommandAddress));

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AgcExports.SetPacketPredication(ctx));
        Assert.Equal(0xC002_3F00u, ReadUInt32(memory, CommandAddress));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateCommandContext()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CommandEndAddress);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
