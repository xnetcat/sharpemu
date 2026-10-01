// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AprCompletionRangesTests
{
    [Fact]
    public void OutOfOrderCompletionsCoalesceAndConsumeExactlyOnce()
    {
        var ranges = new AprCompletionRanges();
        for (uint id = 2; id <= 20000; id += 2) ranges.Add(id, 0);
        for (uint id = 1; id <= 20000; id += 2) ranges.Add(id, 0);
        Assert.Equal(1, ranges.RangeCount);
        foreach (var id in new uint[] { 10000, 1, 20000, 9999, 10001 })
        {
            Assert.True(ranges.TryTake(id, out var result));
            Assert.Equal(0, result);
            Assert.False(ranges.TryTake(id, out _));
        }
        Assert.True(ranges.Contains(10002));
        Assert.False(ranges.Contains(10001));
        ranges.Add(uint.MaxValue, 12);
        ranges.Add(uint.MaxValue - 1, 12);
        Assert.True(ranges.TryTake(uint.MaxValue, out var error));
        Assert.Equal(12, error);
        Assert.True(ranges.TryTake(uint.MaxValue - 1, out error));
        Assert.Equal(12, error);
    }
}
