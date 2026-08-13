// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryProducedRetentionTests
{
    private const ulong WatchedLabel = 0x7020_0000_1000UL;

    // A suspended DCB whose label the guest has recycled can only be released by
    // replaying the value a real producer wrote to that label. Recording enough
    // unrelated producers to cross the table's soft bound must not discard that
    // value, or the waiter is stranded and the graphics queue never resumes.
    [Fact]
    public void ProducedValueSurvivesBoundCrossingWhileAWaiterWatchesIt()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));

        // Cross the soft bound with labels nobody is waiting on.
        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // The guest has since recycled the label, so its memory no longer holds
        // the produced value — the registry's record is the only way back.
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void UnwatchedProducedValuesArePrunedAtTheBound()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // Nothing was watching any of them, so a waiter registered afterwards on
        // a pruned label has no produced value to replay and stays suspended.
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.Null(broken);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ProducerlessAgedWaitersAreCollectedAfterTheDeadline()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));

        Assert.Null(GpuWaitRegistry.CollectProducerlessAged(memory, nowTicks: 100, minAgeTicks: 500));

        var broken = GpuWaitRegistry.CollectProducerlessAged(memory, nowTicks: 1_000, minAgeTicks: 500);
        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ProducerlessBreakSkipsOnlyWhenProducedValueSatisfiesTheWait()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));

        Assert.Null(GpuWaitRegistry.CollectProducerlessAged(memory, nowTicks: 1_000, minAgeTicks: 1));

        // Unusable produced value must not block the producerless path forever.
        GpuWaitRegistry.Clear();
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        _ = GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0xDEAD);
        var broken = GpuWaitRegistry.CollectProducerlessAged(memory, nowTicks: 1_000, minAgeTicks: 1);
        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void RecordProducedLatchesLiveWaiterImmediately()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));

        // WRITE_DATA / EVENT_WRITE producers must latch an already-waiting DCB
        // without waiting for the next DrainResumableDcbs memory poll.
        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));
        var snapshot = GpuWaitRegistry.SnapshotOutstanding(memory);
        Assert.Equal(1, snapshot.Outstanding);
        Assert.Equal(1, snapshot.Latched);
        // Latched waiters must not be treated as producerless.
        Assert.Null(GpuWaitRegistry.CollectProducerlessAged(memory, nowTicks: 1_000, minAgeTicks: 1));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void MarkPastWaitProducersScannedIsStickyPerWaiter()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        waiter.ResumeAddress = 0x1000;
        waiter.SubmissionId = 7;
        GpuWaitRegistry.Register(WatchedLabel, waiter);

        GpuWaitRegistry.MarkPastWaitProducersScanned(memory, 0x1000, "dcb.graphics", 7);
        var snapshot = GpuWaitRegistry.SnapshotWaiters(memory);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!, w => w.PastWaitProducersScanned && w.ResumeAddress == 0x1000);

        GpuWaitRegistry.ClearPastWaitProducersScanned(memory);
        snapshot = GpuWaitRegistry.SnapshotWaiters(memory);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!, w => !w.PastWaitProducersScanned && w.ResumeAddress == 0x1000);
        GpuWaitRegistry.Clear();
    }

    private static GpuWaitRegistry.WaitingDcb NewWaiter(object memory, ulong address) => new()
    {
        WaitAddress = address,
        ReferenceValue = 1,
        Mask = 0xFFFF_FFFFUL,
        CompareFunction = 3, // equal
        Memory = memory,
        QueueName = "dcb.graphics",
        RegisteredTicks = 0,
    };
}
