// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDescriptorChainTests
{
    [Fact]
    public void ResolvesMultiplePointerLoadsAndMasksBufferDescriptorStride()
    {
        var memory = new FakeCpuMemory(0x1000, 0x4000);
        WriteUInt32(memory, 0x1100, 0x2000);
        WriteUInt32(memory, 0x1104, 0);
        WriteUInt32(memory, 0x2020, 0x3000);
        WriteUInt32(memory, 0x2024, 0xABCD_0000);
        var chain = new Gen5DescriptorChain(
            0x1100,
            0x1104,
            [
                (0x20, false),
                (0x44, true),
            ]);

        Assert.True(VulkanVideoPresenter.TryResolveDescriptorChainAddress(
            memory,
            chain,
            out var address));
        Assert.Equal(0x3044UL, address);
    }

    [Fact]
    public void RejectsAnUnwrittenPointer()
    {
        var memory = new FakeCpuMemory(0x1000, 0x1000);
        var chain = new Gen5DescriptorChain(
            0x1100,
            0x1104,
            [(0x20, false)]);

        Assert.False(VulkanVideoPresenter.TryResolveDescriptorChainAddress(
            memory,
            chain,
            out var address));
        Assert.Equal(0UL, address);
    }

    private static void WriteUInt32(
        FakeCpuMemory memory,
        ulong address,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
