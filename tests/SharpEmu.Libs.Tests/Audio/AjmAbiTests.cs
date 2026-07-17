// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AjmAbiTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;

    [Theory]
    [InlineData(Generation.Gen4, 0UL)]
    [InlineData(Generation.Gen5, 0UL)]
    [InlineData(Generation.Gen5, 0x0000_0001_0000_0000UL)]
    [InlineData(Generation.Gen5, 0x0000_0002_0000_0000UL)]
    [InlineData(Generation.Gen5, 0x0000_0003_0000_0000UL)]
    public void Initialize_AcceptsGenerationSpecificFlags(
        Generation generation,
        ulong initializeFlags)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        context[CpuRegister.Rdi] = initializeFlags;
        context[CpuRegister.Rsi] = OutputAddress;

        var result = AjmExports.AjmInitialize(context);

        Span<byte> output = stackalloc byte[sizeof(uint)];
        Assert.Equal(0, result);
        Assert.True(memory.TryRead(OutputAddress, output));
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(output));
    }

    [Theory]
    [InlineData(Generation.Gen4, 0x0000_0003_0000_0000UL)]
    [InlineData(Generation.Gen5, 0x0000_0004_0000_0000UL)]
    [InlineData(Generation.Gen5, 0x0000_0003_0000_0001UL)]
    public void Initialize_RejectsUnsupportedOrNonReservedBits(
        Generation generation,
        ulong initializeFlags)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        context[CpuRegister.Rdi] = initializeFlags;
        context[CpuRegister.Rsi] = OutputAddress;

        var result = AjmExports.AjmInitialize(context);

        Assert.Equal(unchecked((int)0x806A0001), result);
    }
}
