// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class MaskedDwordCopyTests
{
    [Theory]
    [InlineData(0u, new uint[] { 11, 11, 11, 11, 11, 11 })]
    [InlineData(1u, new uint[] { 11, 22, 11, 22, 11, 22 })]
    [InlineData(3u, new uint[] { 11, 22, 33, 0, 11, 22 })]
    public void MaskedCopyPreservesSourceSnapshotAndExactOutputBounds(uint mask, uint[] expected)
    {
        var memory = new FakeCpuMemory(0x1000, 128);
        var initial = Enumerable.Repeat((byte)0xCD, 128).ToArray();
        Assert.True(memory.TryWrite(0x1000, initial));
        Assert.True(memory.TryWrite(0x1000, MemoryMarshal.AsBytes(new uint[] { 11, 22, 33 }.AsSpan())));
        Assert.True(ShaderPipelineCache.TryCopyMaskedDwords(memory, 0x1000, 12, 0x1004, 6, mask));
        var output = new byte[128];
        Assert.True(memory.TryRead(0x1000, output));
        Assert.Equal(11u, MemoryMarshal.Read<uint>(output));
        Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(output.AsSpan(4, 24)).ToArray());
        Assert.All(output[28..], value => Assert.Equal((byte)0xCD, value));
    }

    [Fact]
    public void FailedReadLeavesDestinationUnchanged()
    {
        var memory = new FakeCpuMemory(0x1000, 128);
        Assert.True(memory.TryWrite(0x1000, new byte[] { 0xAB }));
        Assert.False(ShaderPipelineCache.TryCopyMaskedDwords(memory, 0x2000, 4, 0x1000, 16, 0));
        Span<byte> result = stackalloc byte[1];
        Assert.True(memory.TryRead(0x1000, result));
        Assert.Equal(0xAB, result[0]);
        Assert.False(ShaderPipelineCache.TryCopyMaskedDwords(memory, 0x1000, 4, 0x2000, 16, 0));
    }

    [Fact]
    public void RepeatedLargeCopiesReuseScratchStorage()
    {
        var memory = new FakeCpuMemory(0x1000, 4 * 1024 * 1024);
        const uint count = 1024 * 1024;
        Assert.True(ShaderPipelineCache.TryCopyMaskedDwords(memory, 0x1000, 4, 0x1000, count, 0));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 8; i++)
            Assert.True(ShaderPipelineCache.TryCopyMaskedDwords(memory, 0x1000, 4, 0x1000, count, 0));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
    }
}
