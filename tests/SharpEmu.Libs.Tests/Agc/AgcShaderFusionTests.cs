// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcShaderFusionTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong ResultAddress = BaseAddress + 0x100;
    private const ulong FrontShaderAddress = BaseAddress + 0x200;
    private const ulong BackShaderAddress = BaseAddress + 0x300;
    private const ulong FrontRegistersAddress = BaseAddress + 0x500;
    private const ulong BackRegistersAddress = BaseAddress + 0x600;
    private const ulong ScratchAddress = BaseAddress + 0x700;
    private const ulong FrontCodeAddress = 0x0000_1234_5678_9A00;

    [Fact]
    public void GetFusedShaderSize_GeometryHalves_ReturnsBackRegisterStorage()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteByte(memory, FrontShaderAddress + 0x5A, 4);
        WriteByte(memory, BackShaderAddress + 0x5A, 6);
        WriteByte(memory, BackShaderAddress + 0x5C, 4);

        ctx[CpuRegister.Rdi] = ResultAddress;
        ctx[CpuRegister.Rsi] = FrontShaderAddress;
        ctx[CpuRegister.Rdx] = BackShaderAddress;

        var result = AgcExports.GetFusedShaderSize(ctx);

        Assert.Equal(0, result);
        Assert.Equal(32UL, ReadUInt64(memory, ResultAddress));
        Assert.Equal(4UL, ReadUInt64(memory, ResultAddress + 8));
    }

    [Fact]
    public void FuseShaderHalves_GeometryHalves_CopiesAndPatchesRegisterTable()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, FrontShaderAddress + 0x10, FrontCodeAddress);
        WriteUInt64(memory, FrontShaderAddress + 0x20, FrontRegistersAddress);
        WriteByte(memory, FrontShaderAddress + 0x5A, 4);
        WriteByte(memory, FrontShaderAddress + 0x5C, 2);
        WriteRegister(memory, FrontRegistersAddress + 0x00, 0x80, 0x1111_1111);
        WriteRegister(memory, FrontRegistersAddress + 0x08, 0x80, 0x2222_2222);

        WriteUInt64(memory, BackShaderAddress + 0x08, 0xCAFE_BABE);
        WriteUInt64(memory, BackShaderAddress + 0x10, 0x0000_4321_0000_0000);
        WriteUInt64(memory, BackShaderAddress + 0x20, BackRegistersAddress);
        WriteByte(memory, BackShaderAddress + 0x5A, 6);
        WriteByte(memory, BackShaderAddress + 0x5C, 4);
        WriteRegister(memory, BackRegistersAddress + 0x00, 0x80, 0xAAAA_AAAA);
        WriteRegister(memory, BackRegistersAddress + 0x08, 0x80, 0xBBBB_BBBB);
        WriteRegister(memory, BackRegistersAddress + 0x10, 0xC8, 0xCCCC_CCCC);
        WriteRegister(memory, BackRegistersAddress + 0x18, 0xC9, 0xDDDD_DD00);

        ctx[CpuRegister.Rdi] = ResultAddress;
        ctx[CpuRegister.Rsi] = FrontShaderAddress;
        ctx[CpuRegister.Rdx] = BackShaderAddress;
        ctx[CpuRegister.Rcx] = ScratchAddress;

        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ReadUInt64(memory, ResultAddress + 0x08));
        Assert.Equal(ScratchAddress, ReadUInt64(memory, ResultAddress + 0x20));
        Assert.Equal(2, ReadByte(memory, ResultAddress + 0x5A));
        Assert.Equal(0x1111_1111U, ReadUInt32(memory, ScratchAddress + 0x04));
        Assert.Equal(0x2222_2222U, ReadUInt32(memory, ScratchAddress + 0x0C));
        Assert.Equal(0x3456_789AU, ReadUInt32(memory, ScratchAddress + 0x14));
        Assert.Equal(0xDDDD_DD12U, ReadUInt32(memory, ScratchAddress + 0x1C));
    }

    [Fact]
    public void FuseShaderHalves_MismatchedTypes_ReturnsInvalidHalves()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteByte(memory, FrontShaderAddress + 0x5A, 4);
        WriteByte(memory, BackShaderAddress + 0x5A, 7);

        ctx[CpuRegister.Rdi] = ResultAddress;
        ctx[CpuRegister.Rsi] = FrontShaderAddress;
        ctx[CpuRegister.Rdx] = BackShaderAddress;

        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal(unchecked((int)0x8A6C0008), result);
    }

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[1];
        Assert.True(memory.TryRead(address, buffer));
        return buffer[0];
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value) =>
        Assert.True(memory.TryWrite(address, stackalloc byte[] { value }));

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteRegister(
        FakeCpuMemory memory,
        ulong address,
        uint offset,
        uint value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
