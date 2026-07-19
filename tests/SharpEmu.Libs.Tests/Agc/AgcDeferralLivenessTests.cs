// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Liveness invariant for the WAIT_REG_MEM defer machinery: a guest-visible
// producer write (RELEASE_MEM / DMA label write) that has been parked on a
// queue's deferred-effects pile must always eventually flow to a wait that
// overlaps it, otherwise presentation wedges permanently on a label nobody
// ever stores to. A producer that does not overlap the wait must stay parked
// (never spuriously run, which would break intra-queue ordering).
public sealed class AgcDeferralLivenessTests
{
    [Fact]
    public void DeferredProducer_OverlappingWait_Flows()
    {
        var (ran, retained) = AgcExports.ReleaseDeferredProducerForTest(
            producerAddress: 0x7020F77620,
            producerLength: sizeof(uint),
            waitAddress: 0x7020F77620,
            waitLength: sizeof(uint));

        Assert.True(ran, "trapped producer overlapping the wait must flow");
        Assert.False(retained, "released producer must leave the deferred pile");
    }

    [Fact]
    public void DeferredProducer_PartiallyOverlappingWait_Flows()
    {
        // A 64-bit wait covers eight bytes; a 32-bit producer landing in the
        // upper half still feeds it.
        var (ran, retained) = AgcExports.ReleaseDeferredProducerForTest(
            producerAddress: 0x7020F77624,
            producerLength: sizeof(uint),
            waitAddress: 0x7020F77620,
            waitLength: sizeof(ulong));

        Assert.True(ran);
        Assert.False(retained);
    }

    [Fact]
    public void DeferredProducer_NonOverlappingWait_StaysParked()
    {
        var (ran, retained) = AgcExports.ReleaseDeferredProducerForTest(
            producerAddress: 0x7020F77640,
            producerLength: sizeof(uint),
            waitAddress: 0x7020F77620,
            waitLength: sizeof(uint));

        Assert.False(ran, "unrelated producer must not be run early");
        Assert.True(retained, "unrelated producer must remain deferred");
    }

    [Fact]
    public void MonitorDiscoversOutstandingWaitRanges()
    {
        var memory = new object();
        GpuWaitRegistry.Clear();
        try
        {
            GpuWaitRegistry.Register(0x1000, new GpuWaitRegistry.WaitingDcb
            {
                Memory = memory,
                WaitAddress = 0x1000,
                Is64Bit = false,
            });
            GpuWaitRegistry.Register(0x2000, new GpuWaitRegistry.WaitingDcb
            {
                Memory = memory,
                WaitAddress = 0x2000,
                Is64Bit = true,
            });
            // A waiter bound to a different guest memory must not be reported.
            GpuWaitRegistry.Register(0x3000, new GpuWaitRegistry.WaitingDcb
            {
                Memory = new object(),
                WaitAddress = 0x3000,
                Is64Bit = false,
            });

            var ranges = GpuWaitRegistry.SnapshotWaitRanges(memory);

            Assert.NotNull(ranges);
            Assert.Equal(2, ranges!.Count);
            Assert.Contains((0x1000UL, (ulong)sizeof(uint)), ranges);
            Assert.Contains((0x2000UL, (ulong)sizeof(ulong)), ranges);
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }
}
