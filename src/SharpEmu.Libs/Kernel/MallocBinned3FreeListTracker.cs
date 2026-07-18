// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Tracks MallocBinned3 per-thread cache arrays and detects whether a 0x20-byte
/// AGC completion record has already been returned to a free list.
/// </summary>
internal static class MallocBinned3FreeListTracker
{
    private const int MaxPoolCandidates = 256;
    private const int MaxChainHops = 4096;
    private const ulong PoolAlignmentMask = 0xFFFF;
    private const ulong BlockAlignmentMask = 0x1F;
    private static readonly long[] PoolCandidates = new long[MaxPoolCandidates];
    private static readonly ConcurrentDictionary<ulong, int> SuppressedDmaDebt = new();
    private static int _poolCandidateCount;

    internal readonly record struct FreeListMatch(
        ulong PoolBase,
        ulong Head,
        int Hops,
        int List);

    internal static void NoteTlsPoolCandidate(ulong address)
    {
        // MallocBinned3's per-thread pool arrays are 64 KiB allocations. The
        // shape gate makes unrelated pthread TLS traffic a no-op.
        if (address < 0x10000 || (address & PoolAlignmentMask) != 0)
        {
            return;
        }

        var count = Math.Min(
            Volatile.Read(ref _poolCandidateCount),
            MaxPoolCandidates);
        for (var index = 0; index < count; index++)
        {
            if (unchecked((ulong)Volatile.Read(ref PoolCandidates[index])) == address)
            {
                return;
            }
        }

        var slot = Interlocked.Increment(ref _poolCandidateCount) - 1;
        if ((uint)slot < PoolCandidates.Length)
        {
            Interlocked.Exchange(
                ref PoolCandidates[slot],
                unchecked((long)address));
        }
    }

    internal static bool TryFindFreeBlock(
        CpuContext ctx,
        ulong blockAddress,
        out FreeListMatch match)
    {
        match = default;
        if (!IsPlausibleNode(blockAddress))
        {
            return false;
        }

        if (!TryScanOnce(ctx, blockAddress, out var first))
        {
            return false;
        }

        // A concurrent allocator pop can invalidate a single observation.
        // Require the block to remain linked across two complete scans.
        Thread.MemoryBarrier();
        if (!TryScanOnce(ctx, blockAddress, out var second))
        {
            return false;
        }

        match = second;
        return first.PoolBase == second.PoolBase &&
               first.List == second.List;
    }

    internal static void RecordSuppressedDma(ulong address) =>
        SuppressedDmaDebt.AddOrUpdate(
            address,
            1,
            static (_, count) => checked(count + 1));

    internal static bool TryConsumeSuppressedDma(ulong address)
    {
        while (SuppressedDmaDebt.TryGetValue(address, out var count) && count > 0)
        {
            if (SuppressedDmaDebt.TryUpdate(address, count - 1, count))
            {
                return true;
            }
        }

        return false;
    }

    internal static void ResetForTests()
    {
        Array.Clear(PoolCandidates);
        Volatile.Write(ref _poolCandidateCount, 0);
        SuppressedDmaDebt.Clear();
    }

    private static bool TryScanOnce(
        CpuContext ctx,
        ulong blockAddress,
        out FreeListMatch match)
    {
        match = default;
        var count = Math.Min(
            Volatile.Read(ref _poolCandidateCount),
            MaxPoolCandidates);
        for (var index = 0; index < count; index++)
        {
            var poolBase = unchecked(
                (ulong)Volatile.Read(ref PoolCandidates[index]));
            if (poolBase == 0)
            {
                continue;
            }

            // Each size-class descriptor is
            // {head,count,secondaryHead,secondaryCount}, with a 0x20-byte
            // stride. Index 1 is the 0x20-byte allocation class used by
            // Unreal's consumed-marker records.
            if (!ctx.TryReadUInt64(poolBase + 0x20, out var primaryHead) ||
                !ctx.TryReadUInt64(poolBase + 0x30, out var secondaryHead))
            {
                continue;
            }

            if (TryScanChain(
                    ctx,
                    primaryHead,
                    blockAddress,
                    poolBase,
                    list: 1,
                    out match) ||
                TryScanChain(
                    ctx,
                    secondaryHead,
                    blockAddress,
                    poolBase,
                    list: 2,
                    out match))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryScanChain(
        CpuContext ctx,
        ulong head,
        ulong blockAddress,
        ulong poolBase,
        int list,
        out FreeListMatch match)
    {
        match = default;
        var node = head;
        for (var hops = 0; node != 0 && hops < MaxChainHops; hops++)
        {
            if (node == blockAddress)
            {
                match = new FreeListMatch(poolBase, head, hops, list);
                return true;
            }

            if (!IsPlausibleNode(node) ||
                !ctx.TryReadUInt64(node, out var next) ||
                next == node)
            {
                return false;
            }

            node = next;
        }

        return false;
    }

    private static bool IsPlausibleNode(ulong address) =>
        address >= 0x10000 &&
        (address & BlockAlignmentMask) == 0;
}
