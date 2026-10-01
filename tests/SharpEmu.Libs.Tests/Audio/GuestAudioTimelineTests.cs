// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class GuestAudioTimelineTests
{
    [Fact]
    public void LaterStreamAdvancesWithoutReplayingEarlierStreamDuration()
    {
        var timeline = new GuestAudioTimeline();
        var ambient = timeline.CreateSource();
        ambient.Report(60);

        var movie = timeline.CreateSource();
        movie.Report(0);
        Assert.Equal(60, timeline.PlayedSeconds);
        movie.Report(1);
        Assert.Equal(61, timeline.PlayedSeconds);

        // The older stream can run slower without holding the movie clock back.
        ambient.Report(60.2);
        Assert.Equal(61, timeline.PlayedSeconds);
        movie.Report(2);
        Assert.Equal(62, timeline.PlayedSeconds);
    }

    [Fact]
    public void ConcurrentStreamsDoNotAddTheirDurationsTogether()
    {
        var timeline = new GuestAudioTimeline();
        var first = timeline.CreateSource();
        var second = timeline.CreateSource();
        first.Report(1);
        second.Report(1);
        Assert.Equal(1, timeline.PlayedSeconds);
        second.Report(0.5);
        Assert.Equal(1, timeline.PlayedSeconds);
    }

    [Fact]
    public void InvalidPositionsDoNotAdvanceOrStartTheClock()
    {
        var timeline = new GuestAudioTimeline();
        var source = timeline.CreateSource();
        source.Report(double.NaN);
        source.Report(double.PositiveInfinity);
        source.Report(double.NegativeInfinity);
        source.Report(-1);
        source.Report(0);
        Assert.Equal(0, timeline.PlayedSeconds);
        Assert.False(timeline.IsRunning);
    }
}
