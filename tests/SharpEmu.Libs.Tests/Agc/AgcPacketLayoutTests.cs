// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPacketLayoutTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CommandAddress = BaseAddress + 0x1000;
    private const ulong StackAddress = BaseAddress + 0x3000;

    [Fact]
    public void DcbWaitRegMem_32Bit_UsesSevenDwordAgcLayout()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 4;
        ctx[CpuRegister.R8] = 2;
        ctx[CpuRegister.R9] = 0xFFFF_FFFF_1234_567B;
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 0xFFFF_FFFF_AABB_CCDD);
        WriteUInt32(memory, StackAddress + 24, 0x1_2340);

        Assert.Equal(0, AgcExports.DcbWaitRegMem(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + (7 * sizeof(uint)), ReadUInt64(memory, CommandBufferAddress + 0x10));

        Assert.Equal(0xC005_1028u, ReadUInt32(memory, CommandAddress + 0));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x0003_FFFFu, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0xAABB_CCDDu, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, CommandAddress + 16));
        Assert.Equal(0x0400_0053u, ReadUInt32(memory, CommandAddress + 20));
        Assert.Equal(0x0000_1234u, ReadUInt32(memory, CommandAddress + 24));
    }

    [Fact]
    public void AcbWaitRegMem_64Bit_UsesNineDwordAgcLayout()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 6;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = 0xFFFF_FFFF_1234_567F;
        ctx[CpuRegister.R9] = 0x1122_3344_5566_7788;
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, 0x99AA_BBCC_DDEE_FF00);
        WriteUInt32(memory, StackAddress + 16, 0x20);

        Assert.Equal(0, AgcExports.AcbWaitRegMem(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);

        Assert.Equal(0xC007_1058u, ReadUInt32(memory, CommandAddress + 0));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x0003_FFFFu, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0xDDEE_FF00u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x99AA_BBCCu, ReadUInt32(memory, CommandAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, CommandAddress + 20));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, CommandAddress + 24));
        Assert.Equal(0x0200_0016u, ReadUInt32(memory, CommandAddress + 28));
        Assert.Equal(2u, ReadUInt32(memory, CommandAddress + 32));
    }

    [Fact]
    public void CbReleaseMem_UsesHardwareBitfieldsAndSpecialCases()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x30;
        ctx[CpuRegister.Rdx] = 0x100;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = 2;
        ctx[CpuRegister.R9] = 0x1234_5678_9ABC_DEF3;
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, 3);
        WriteUInt64(memory, StackAddress + 16, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 24, 0);
        WriteUInt64(memory, StackAddress + 32, 0);
        WriteUInt64(memory, StackAddress + 40, 2);
        WriteUInt64(memory, StackAddress + 48, 0xFFFF_FFFF);

        Assert.Equal(0, AgcExports.CbReleaseMem(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);

        Assert.Equal(0xC006_1060u, ReadUInt32(memory, CommandAddress + 0));
        Assert.Equal(0x0430_0630u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x6201_0000u, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0x9ABC_DEF0u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, CommandAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, CommandAddress + 20));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, CommandAddress + 24));
        Assert.Equal(0x07FF_FFFFu, ReadUInt32(memory, CommandAddress + 28));
    }

    [Fact]
    public void EndOfPipePatches_UseTheirFullAbiAndPacketFields()
    {
        var (memory, ctx) = CreateContext();
        BuildReleasePacket(memory, ctx);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x102;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = 0xABCD_EF12;
        Assert.Equal(0, AgcExports.QueueEndOfPipeActionPatchData(ctx));
        Assert.Equal(0x0000_0001_00CD_EF12UL, ReadUInt64(memory, CommandAddress + 20));

        ctx[CpuRegister.Rsi] = 5;
        Assert.Equal(0, AgcExports.QueueEndOfPipeActionPatchType(ctx));
        Assert.Equal(5u, ReadUInt32(memory, CommandAddress + 8) >> 29);

        ctx[CpuRegister.Rsi] = 0x100;
        Assert.Equal(0, AgcExports.QueueEndOfPipeActionPatchGcrCntl(ctx));
        Assert.Equal(0x300u, (ReadUInt32(memory, CommandAddress + 4) >> 12) & 0xFFFu);
    }

    [Fact]
    public void WaitPatches_TargetReferenceAndControlWithoutSwappingThem()
    {
        var (memory, ctx) = CreateContext();
        BuildWait32Packet(memory, ctx);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0xA1B2_C3D4;
        Assert.Equal(0, AgcExports.WaitRegMemPatchReference(ctx));
        Assert.Equal(0xA1B2_C3D4u, ReadUInt32(memory, CommandAddress + 16));

        ctx[CpuRegister.Rsi] = 6;
        Assert.Equal(0, AgcExports.WaitRegMemPatchCompareFunction(ctx));
        Assert.Equal(6u, ReadUInt32(memory, CommandAddress + 20) & 7u);

        ctx[CpuRegister.Rsi] = 0xFFFF_FFFF_1234_567B;
        Assert.Equal(0, AgcExports.WaitRegMemPatchAddress(ctx));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x0003_FFFFu, ReadUInt32(memory, CommandAddress + 8));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x8000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CommandAddress + 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0);
        WriteUInt32(memory, CommandBufferAddress + 0x30, 0);
        return (memory, ctx);
    }

    private static void BuildReleasePacket(FakeCpuMemory memory, CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x28;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = BaseAddress + 0x5000;
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, 1);
        WriteUInt64(memory, StackAddress + 16, 1);
        WriteUInt64(memory, StackAddress + 24, 0);
        WriteUInt64(memory, StackAddress + 32, 0);
        WriteUInt64(memory, StackAddress + 40, 0);
        WriteUInt64(memory, StackAddress + 48, 0);
        Assert.Equal(0, AgcExports.CbReleaseMem(ctx));
    }

    private static void BuildWait32Packet(FakeCpuMemory memory, CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = BaseAddress + 0x6000;
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, 1);
        WriteUInt64(memory, StackAddress + 16, uint.MaxValue);
        WriteUInt32(memory, StackAddress + 24, 0x10);
        Assert.Equal(0, AgcExports.DcbWaitRegMem(ctx));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
