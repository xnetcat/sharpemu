// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.Host;

/// <summary>
/// How much guest audio the host device has actually played, in seconds.
///
/// This is the only clock in the emulator that advances at the rate the player
/// hears. Wall clock runs ahead of it whenever the guest cannot feed the device
/// (the stream underruns and the missing time is never played), so anything
/// that has to stay in step with the guest's audio — host-decoded video being
/// the case that matters — has to follow this rather than <see cref="Stopwatch"/>.
///
/// Stream sample positions are aligned to their starting point on this timeline.
/// The leading active stream drives playback; a newly started movie does not
/// have to catch up with an older ambient stream's lifetime sample count.
/// </summary>
public static class GuestAudioClock
{
    private static readonly GuestAudioTimeline Timeline = new();

    public static double PlayedSeconds => Timeline.PlayedSeconds;
    public static bool IsRunning => Timeline.IsRunning;
    internal static GuestAudioTimeline.Source CreateSource() => Timeline.CreateSource();
}

/// <summary>
/// Aligns stream-relative sample positions to one playback timeline. A stream
/// opened after another has played must not spend that earlier duration catching
/// up before its progress can drive synchronized video.
/// </summary>
internal sealed class GuestAudioTimeline
{
    private long _playedMicroseconds;
    private long _lastAdvanceTimestamp;

    /// <summary>Seconds of guest audio the device has played. Monotonic.</summary>
    public double PlayedSeconds =>
        Interlocked.Read(ref _playedMicroseconds) / 1_000_000.0;

    /// <summary>
    /// True while a stream has reported progress recently. False means no guest
    /// audio is playing, and callers must fall back to wall clock rather than
    /// stalling on a clock that will never advance.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            var last = Interlocked.Read(ref _lastAdvanceTimestamp);
            return last != 0 &&
                   Stopwatch.GetElapsedTime(last) < TimeSpan.FromMilliseconds(250);
        }
    }

    private void Report(double playedSeconds)
    {
        if (!double.IsFinite(playedSeconds) || playedSeconds < 0)
        {
            return;
        }

        var microseconds = (long)(playedSeconds * 1_000_000.0);
        var current = Interlocked.Read(ref _playedMicroseconds);
        while (microseconds > current)
        {
            var seen = Interlocked.CompareExchange(
                ref _playedMicroseconds,
                microseconds,
                current);
            if (seen == current)
            {
                Interlocked.Exchange(ref _lastAdvanceTimestamp, Stopwatch.GetTimestamp());
                return;
            }

            current = seen;
        }
    }

    internal Source CreateSource() => new(this, PlayedSeconds);

    internal sealed class Source(GuestAudioTimeline timeline, double originSeconds)
    {
        internal void Report(double playedSeconds)
        {
            if (!double.IsFinite(playedSeconds) || playedSeconds < 0)
            {
                return;
            }

            timeline.Report(originSeconds + playedSeconds);
        }
    }
}
