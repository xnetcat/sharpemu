// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class ReadbackPrefetchTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = 16 * 1024;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NeighborReadsShareOneDrainAndPreserveSurroundingBytes(bool enabled, bool cpuWrite)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.ReadbackPrefetch = enabled;
        var address = harness.MapBacked(4 * Page, ReadWrite);
        harness.Write(address, Enumerable.Repeat((byte)0x37, (int)(4 * Page)).ToArray());
        harness.Worker.Run(() =>
        {
            Fill(harness, address + 8, 4, 0x12345678);
            Fill(harness, address + 2 * Page + 8, 4, 0xABCDEF01);
            Assert.Equal(2, harness.Cache.BufferCount);
            harness.Cache.ReadMemory(address + 8, 4, cpuWrite);
            Assert.Equal(!enabled, harness.Cache.HasGpuDirtyBytes(address + 2 * Page, Page));
            Assert.Equal(cpuWrite, harness.Cache.HasCpuDirtyPages(address, 4));
            var tick = harness.Scheduler.CurrentTick;
            harness.Cache.ReadMemory(address + 2 * Page + 8, 4);
            if (enabled) Assert.Equal(tick, harness.Scheduler.CurrentTick);
            else Assert.True(harness.Scheduler.CurrentTick > tick);
            var expected = Enumerable.Repeat((byte)0x37, 16).ToArray();
            BitConverter.GetBytes(0xABCDEF01u).CopyTo(expected, 8);
            Assert.Equal(expected, harness.Read(address + 2 * Page, 16));

            // A subsequent GPU rewrite must invalidate the prefetched version.
            Fill(harness, address + 2 * Page + 8, 4, 0x87654321);
            harness.Cache.ReadMemory(address + 2 * Page + 8, 4);
            Assert.Equal(BitConverter.GetBytes(0x87654321u), harness.Read(address + 2 * Page + 8, 4));
        });
    }

    [Fact]
    public void PrefetchPreservesCpuDirtyPagesInTheSameAllocation()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.ReadbackPrefetch = true;
        var address = harness.MapBacked(12 * Page, ReadWrite);
        var neighbor = address + 4 * Page;
        harness.Worker.Run(() =>
        {
            _ = harness.Cache.FindBuffer(neighbor, 4 * Page);
            harness.Cache.SynchronizeBuffersInRange(neighbor, 4 * Page);
            Fill(harness, address, 4, 1);
            Fill(harness, neighbor + 8, 4, 2);
            Assert.True(harness.Store.MarkCpuWrite(neighbor + 2 * Page, 4));
            harness.Write(neighbor + 2 * Page, [7, 8, 9, 10]);
            harness.Cache.ReadMemory(address, 4);
            Assert.False(harness.Cache.HasGpuDirtyBytes(neighbor, 4 * Page));
            Assert.True(harness.Cache.HasCpuDirtyPages(neighbor + 2 * Page, 4));
            Assert.Equal(new byte[] { 7, 8, 9, 10 }, harness.Read(neighbor + 2 * Page, 4));
            Assert.Equal(BitConverter.GetBytes(2u), harness.Read(neighbor + 8, 4));
        });
    }

    [Fact]
    public void PrefetchDoesNotConsumeANewerWriter()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.ReadbackPrefetch = true;
        var address = harness.MapBacked(4 * Page, ReadWrite);
        harness.Worker.Run(() =>
        {
            Fill(harness, address, 4, 1);
            harness.Scheduler.Flush();
            Fill(harness, address + 2 * Page, 4, 2);
            harness.Cache.ReadMemory(address, 4);
            Assert.True(harness.Cache.HasGpuDirtyBytes(address + 2 * Page, 4));
            harness.Cache.ReadMemory(address + 2 * Page, 4);
            Assert.Equal(BitConverter.GetBytes(2u), harness.Read(address + 2 * Page, 4));
        });
    }

    [Fact]
    public void PrefetchBoundsBytesAndAddressWindowWithoutClearingSkippedOwners()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.ReadbackPrefetch = true;
        var address = harness.MapBacked(1024 * 1024, ReadWrite);
        Assert.Equal(0UL, address % (512 * 1024));
        harness.Worker.Run(() =>
        {
            Fill(harness, address, 4, 1);
            Fill(harness, address + 4 * Page, 5 * Page, 2); // Exceeds the extra-byte budget.
            Fill(harness, address + 12 * Page, 4, 3);
            Fill(harness, address + 40 * Page, 4, 4); // Outside the address window.
            harness.Cache.ReadMemory(address, 4);
            Assert.True(harness.Cache.HasGpuDirtyBytes(address + 4 * Page, 5 * Page));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address + 12 * Page, 4));
            Assert.True(harness.Cache.HasGpuDirtyBytes(address + 40 * Page, 4));
            Assert.Equal(BitConverter.GetBytes(3u), harness.Read(address + 12 * Page, 4));
        });
    }

    [Fact]
    public void PrefetchBoundsTheNumberOfNeighborAllocations()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.ReadbackPrefetch = true;
        var address = harness.MapBacked(32 * Page, ReadWrite);
        harness.Worker.Run(() =>
        {
            for (var index = 0; index < 20; index++) Fill(harness, address + (ulong)index * Page, 4, (uint)index);
            Assert.Equal(20, harness.Cache.BufferCount);
            harness.Cache.ReadMemory(address, 4);
            for (var index = 1; index <= 16; index++)
                Assert.False(harness.Cache.HasGpuDirtyBytes(address + (ulong)index * Page, 4));
            for (var index = 17; index < 20; index++)
                Assert.True(harness.Cache.HasGpuDirtyBytes(address + (ulong)index * Page, 4));
        });
    }

    private static void Fill(CacheHarness harness, ulong address, ulong size, uint value)
    {
        var target = harness.Cache.ObtainBuffer(address, size, true);
        target.Buffer.Fill(target.Offset, size, value);
    }
}
