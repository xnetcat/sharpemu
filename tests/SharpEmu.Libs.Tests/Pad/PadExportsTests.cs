// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Fact]
    public void AutoCrossCatchUp_ReplaysMissedPressesWithReleaseGaps()
    {
        double[] times = [28, 36, 44];
        var nextIndex = 0;
        var activeUntil = 0.0;
        var releaseUntil = 0.0;

        Assert.True(PadExports.TryAdvanceAutoCrossSequence(
            100.0,
            times,
            ref nextIndex,
            ref activeUntil,
            ref releaseUntil,
            out var activatedIndex));
        Assert.Equal(0, activatedIndex);
        Assert.Equal(1, nextIndex);

        Assert.True(PadExports.TryAdvanceAutoCrossSequence(
            100.2,
            times,
            ref nextIndex,
            ref activeUntil,
            ref releaseUntil,
            out activatedIndex));
        Assert.Equal(-1, activatedIndex);

        Assert.False(PadExports.TryAdvanceAutoCrossSequence(
            100.6,
            times,
            ref nextIndex,
            ref activeUntil,
            ref releaseUntil,
            out activatedIndex));
        Assert.Equal(-1, activatedIndex);

        Assert.True(PadExports.TryAdvanceAutoCrossSequence(
            101.0,
            times,
            ref nextIndex,
            ref activeUntil,
            ref releaseUntil,
            out activatedIndex));
        Assert.Equal(1, activatedIndex);
        Assert.Equal(2, nextIndex);
    }

    [Fact]
    public void AutoCrossCatchUp_WaitsForNextScheduledPress()
    {
        double[] times = [28];
        var nextIndex = 0;
        var activeUntil = 0.0;
        var releaseUntil = 0.0;

        Assert.False(PadExports.TryAdvanceAutoCrossSequence(
            27.9,
            times,
            ref nextIndex,
            ref activeUntil,
            ref releaseUntil,
            out var activatedIndex));
        Assert.Equal(-1, activatedIndex);
        Assert.Equal(0, nextIndex);
    }
}
