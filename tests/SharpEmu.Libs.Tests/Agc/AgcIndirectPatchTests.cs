// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectPatchTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CommandAddress = BaseAddress + 0x400;
    private const ulong CommandEndAddress = BaseAddress + 0x800;

    [Theory]
    [InlineData(30, true, false)]
    [InlineData(31, true, true)]
    [InlineData(32, true, true)]
    [InlineData(31, false, false)]
    public void MetalSingleStageDescriptorLimitBakesOnlyOverflowingScalarState(
        int guestBufferCount,
        bool isMacOs,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.RequiresBakedScalarsForStorageDescriptorLimit(
                guestBufferCount,
                isMacOs));
    }

    [Theory]
    [InlineData(13, true, false)]
    [InlineData(14, true, true)]
    [InlineData(15, true, true)]
    [InlineData(16, true, true)]
    [InlineData(14, false, false)]
    public void MetalGraphicsDescriptorLimitAccountsForBothShaderStages(
        int guestBufferCount,
        bool isMacOs,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.RequiresBakedGraphicsScalarsForStorageDescriptorLimit(
                guestBufferCount,
                isMacOs));
    }

    [Theory]
    [InlineData(2, 4, Gen5PixelOutputKind.Uint)]
    [InlineData(2, 5, Gen5PixelOutputKind.Sint)]
    [InlineData(2, 7, Gen5PixelOutputKind.Float)]
    public void Format16RenderTargetsUseTheirNativePixelOutputKind(
        uint dataFormat,
        uint numberType,
        Gen5PixelOutputKind expected)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                out var format));
        Assert.Equal(expected, format.OutputKind);
    }

    [Fact]
    public void DcbDrawIndirectUsesThreeArgumentAbiAndRealPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CommandEndAddress);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x234;
        ctx[CpuRegister.Rdx] = 0x4000_0000;

        Assert.Equal(0, AgcExports.DcbDrawIndirect(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC003_2400u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x234u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x4000_0000u, ReadUInt32(memory, CommandAddress + 16));
        Assert.Equal(CommandAddress + 20, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void NullPacketPatchHelpersAreSuccessfulNoOps()
    {
        var ctx = new CpuContext(
            new FakeCpuMemory(0x1000, 0x1000),
            Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1234;

        Assert.Equal(0, AgcExports.SetCxRegIndirectPatchSetAddress(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void PatchHelpersUpdateIndirectPacketPayload()
    {
        const ulong packetAddress = 0x1100;
        const ulong registersAddress = 0x1234_5678_9ABC;
        var memory = new FakeCpuMemory(0x1000, 0x1000);
        Span<byte> count = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(count, 3);
        Assert.True(memory.TryWrite(packetAddress + sizeof(uint), count));
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = packetAddress;
        ctx[CpuRegister.Rsi] = registersAddress;
        Assert.Equal(0, AgcExports.SetCxRegIndirectPatchSetAddress(ctx));

        Span<byte> address = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(packetAddress + 8, address));
        Assert.Equal(registersAddress, BinaryPrimitives.ReadUInt64LittleEndian(address));

        ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(0, AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.True(memory.TryRead(packetAddress + sizeof(uint), count));
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32LittleEndian(count));
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

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
