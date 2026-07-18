// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class MallocBinned3FreeListTrackerTests : IDisposable
{
    private const ulong PoolBase = 0x00000000801B0000;
    private const ulong NodeA = 0x0000007020F77000;
    private const ulong NodeB = 0x0000007020F77020;

    public MallocBinned3FreeListTrackerTests() =>
        MallocBinned3FreeListTracker.ResetForTests();

    public void Dispose() =>
        MallocBinned3FreeListTracker.ResetForTests();

    [Fact]
    public void FindsBlockInMallocBinned3IndexOnePrimaryChain()
    {
        var memory = new SparseCpuMemory();
        memory.WriteUInt64(PoolBase + 0x20, NodeA);
        memory.WriteUInt64(PoolBase + 0x30, 0);
        memory.WriteUInt64(NodeA, NodeB);
        memory.WriteUInt64(NodeB, 0);
        var ctx = new CpuContext(memory, Generation.Gen5);
        MallocBinned3FreeListTracker.NoteTlsPoolCandidate(PoolBase);

        Assert.True(MallocBinned3FreeListTracker.TryFindFreeBlock(
            ctx,
            NodeB,
            out var match));
        Assert.Equal(PoolBase, match.PoolBase);
        Assert.Equal(1, match.List);
        Assert.Equal(1, match.Hops);
    }

    [Fact]
    public void RejectsUnlinkedAndMisalignedBlocks()
    {
        var memory = new SparseCpuMemory();
        memory.WriteUInt64(PoolBase + 0x20, NodeA);
        memory.WriteUInt64(PoolBase + 0x30, 0);
        memory.WriteUInt64(NodeA, 0);
        var ctx = new CpuContext(memory, Generation.Gen5);
        MallocBinned3FreeListTracker.NoteTlsPoolCandidate(PoolBase);

        Assert.False(MallocBinned3FreeListTracker.TryFindFreeBlock(
            ctx,
            NodeB,
            out _));
        Assert.False(MallocBinned3FreeListTracker.TryFindFreeBlock(
            ctx,
            NodeA + 1,
            out _));
    }

    [Fact]
    public void SuppressedDmaDebtPairsWithExactlyOneRelease()
    {
        MallocBinned3FreeListTracker.RecordSuppressedDma(NodeA);

        Assert.True(MallocBinned3FreeListTracker.TryConsumeSuppressedDma(NodeA));
        Assert.False(MallocBinned3FreeListTracker.TryConsumeSuppressedDma(NodeA));
    }

    private sealed class SparseCpuMemory : ICpuMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(virtualAddress + (ulong)index, out destination[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
            {
                _bytes[virtualAddress + (ulong)index] = source[index];
            }

            return true;
        }

        public void WriteUInt64(ulong address, ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            TryWrite(address, bytes);
        }
    }
}
