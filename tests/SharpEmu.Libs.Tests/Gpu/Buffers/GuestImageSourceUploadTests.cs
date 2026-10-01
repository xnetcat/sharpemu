// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestImageSourceUploadTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PartialCpuUpdatesPreserveResidentAndGpuOwnedBytes(bool dirtyRangeUploads, bool gpuWritten)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var harness = new CacheHarness(fixture.Vulkan);
        harness.Cache.DirtyImageRangeUploads = dirtyRangeUploads;
        const ulong size = 1024 * 1024;
        var address = harness.MapBacked(size, GuestPageProtection.Read | GuestPageProtection.Write);
        var expected = Enumerable.Repeat((byte)0x37, (int)size).ToArray();
        harness.Write(address, expected);
        GpuBuffer sourceBuffer = null!;
        ulong sourceOffset = 0;
        harness.Worker.Run(() =>
        {
            var owner = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, size));
            harness.Cache.SynchronizeBuffersInRange(address, size);
            harness.Scheduler.Finish();

            if (gpuWritten)
            {
                var gpu = harness.Cache.ObtainBuffer(address + 8 * GuestBufferCache.CachingPageSize, 16, true);
                gpu.Buffer.Fill(gpu.Offset, 16, 0xA5A5A5A5);
                expected.AsSpan(8 * (int)GuestBufferCache.CachingPageSize, 16).Fill(0xA5);
            }

            foreach (var offset in new[] { 3 * (int)GuestBufferCache.CachingPageSize + 17, 12 * (int)GuestBufferCache.CachingPageSize + 31 })
            {
                harness.Gpu.MarkCpuWrite(address + (ulong)offset, 5);
                harness.Write(address + (ulong)offset, [1, 2, 3, 4, 5]);
                new byte[] { 1, 2, 3, 4, 5 }.CopyTo(expected, offset);
            }

            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var before = Position(staging);
            var source = harness.Cache.ObtainBufferForImage(address, size);
            var staged = Position(staging) - before;
            Assert.Same(owner, source.Buffer);
            if (dirtyRangeUploads)
                Assert.InRange(staged, 2 * TrackerLayout.PageBytes, 2 * GuestBufferCache.CachingPageSize);
            else
                Assert.Equal(size, staged);
            Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
            Assert.Equal(gpuWritten, harness.Cache.HasGpuDirtyBytes(address, size));

            before = Position(staging);
            var reused = harness.Cache.ObtainBufferForImage(address, size);
            Assert.Same(owner, reused.Buffer);
            Assert.Equal(before, Position(staging));
            sourceBuffer = source.Buffer;
            sourceOffset = source.Offset;
        });
        // Read the GPU allocation itself, not its possibly stale guest backing.
        Assert.Equal(expected, harness.ReadBack(sourceBuffer, sourceOffset, size));
    }

    private static ulong Position(GpuBuffer buffer) => (ulong)typeof(GpuRingBuffer)
        .GetField("_offset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
}
