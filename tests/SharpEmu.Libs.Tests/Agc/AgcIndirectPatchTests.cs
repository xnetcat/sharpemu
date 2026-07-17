// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectPatchTests
{
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
}
