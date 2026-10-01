// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class VertexReplayIndicesTests
{
    [Fact]
    public void ListPreservesAllIndicesBaseVertexAndCornerIdentity()
    {
        var words = MemoryMarshal.Cast<byte, uint>(VertexReplayIndices.Build([7, 4, 9], PrimitiveTopology.TriangleList, null, -2)).ToArray();
        Assert.Equal(new uint[] { 5, 2, 7, 0, 5, 2, 7, 1, 5, 2, 7, 2 }, words);
    }

    [Fact]
    public void StripPreservesWindingAndRestartsBeforeApplyingBaseVertex()
    {
        var words = MemoryMarshal.Cast<byte, uint>(VertexReplayIndices.Build([1, 2, 3, 4, 99, 5, 6, 7],
            PrimitiveTopology.TriangleStrip, 99, 10)).ToArray();
        Assert.Equal(new uint[] { 11, 12, 13, 0 }, words[..4]);
        Assert.Equal(new uint[] { 12, 14, 13, 0 }, words[12..16]);
        Assert.Equal(new uint[] { 15, 16, 17, 0 }, words[24..28]);
        Assert.Equal(36, words.Length);
    }

    [Fact]
    public void FanKeepsOriginAndIgnoresIncompletePrimitive()
    {
        var words = MemoryMarshal.Cast<byte, uint>(VertexReplayIndices.Build([1, 2, 3, 4, 99, 5, 6],
            PrimitiveTopology.TriangleFan, 99, 0)).ToArray();
        Assert.Equal(new uint[] { 1, 2, 3, 0 }, words[..4]);
        Assert.Equal(new uint[] { 1, 3, 4, 0 }, words[12..16]);
        Assert.Equal(24, words.Length);
    }
    [Fact]
    public void WriterPreservesUnusedDestinationAndAllocatesNothingForLargeMeshes()
    {
        var indices = Enumerable.Range(0, 30000).Select(index => (uint)index).ToArray();
        var size = VertexReplayIndices.MaximumByteCount(indices.Length, PrimitiveTopology.TriangleList);
        var storage = new byte[size + 16];
        storage.AsSpan().Fill(0xCD);
        for (var i = 0; i < 10; i++)
            VertexReplayIndices.Write(indices, PrimitiveTopology.TriangleList, null, -4, storage.AsSpan(8, size));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var written = 0;
        for (var i = 0; i < 100; i++)
            written = VertexReplayIndices.Write(indices, PrimitiveTopology.TriangleList, null, -4, storage.AsSpan(8, size));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(size, written);
        Assert.Equal(0, allocated);
        Assert.All(storage[..8], value => Assert.Equal((byte)0xCD, value));
        Assert.All(storage[^8..], value => Assert.Equal((byte)0xCD, value));
        var words = MemoryMarshal.Cast<byte, uint>(storage.AsSpan(8, size));
        Assert.Equal(unchecked(0u - 4), words[0]);
        Assert.Equal(29995u, words[^2]);
        Assert.Equal(2u, words[^1]);
        Assert.Throws<ArgumentException>(() => VertexReplayIndices.Write(indices,
            PrimitiveTopology.TriangleList, null, 0, new byte[1]));
    }

}
