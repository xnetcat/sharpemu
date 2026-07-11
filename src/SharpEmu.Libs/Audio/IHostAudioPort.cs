// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Host playback sink for one sceAudioOut port. Implementations accept guest
/// sample buffers, convert them to stereo PCM16, and provide pacing through
/// queue backpressure (Submit blocks while the device queue is full).
/// </summary>
internal interface IHostAudioPort : IDisposable
{
    bool Submit(
        ReadOnlySpan<byte> source,
        uint frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume);
}

internal static class AudioSampleConverter
{
    // ITU-R BS.775 downmix weight for the center and surround channels folded
    // into each stereo channel (-3 dB).
    private const float SurroundWeight = 0.7071f;

    /// <summary>
    /// Downmixes an interleaved guest buffer to stereo PCM16, folding center,
    /// LFE and surround channels into the front pair instead of dropping them,
    /// and applying a linear playback volume (1.0 = unity).
    /// </summary>
    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        var gain = Math.Clamp(volume, 0f, 1f);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            DownmixFrame(sourceFrame, channels, bytesPerSample, isFloat, out var left, out var right);
            WriteSample(destination[(frame * 4)..], left * gain);
            WriteSample(destination[((frame * 4) + 2)..], right * gain);
        }
    }

    private static void DownmixFrame(
        ReadOnlySpan<byte> frame,
        int channels,
        int bytesPerSample,
        bool isFloat,
        out float left,
        out float right)
    {
        // Guest layouts: 1=mono, 2=stereo, 6=5.1 (FL FR C LFE BL BR),
        // 8=7.1 (FL FR C LFE BL BR SL SR). Unknown counts fold every extra
        // channel evenly into both outputs so nothing is silently dropped.
        if (channels == 1)
        {
            left = right = ReadSampleFloat(frame, 0, bytesPerSample, isFloat);
            return;
        }

        left = ReadSampleFloat(frame, 0, bytesPerSample, isFloat);
        right = ReadSampleFloat(frame, 1, bytesPerSample, isFloat);
        if (channels < 3)
        {
            return;
        }

        if (channels is 6 or 8)
        {
            var center = ReadSampleFloat(frame, 2, bytesPerSample, isFloat);
            left += SurroundWeight * center;
            right += SurroundWeight * center;

            // Rear/side channels (skip channel 3 = LFE).
            for (var channel = 4; channel < channels; channel++)
            {
                var sample = SurroundWeight * ReadSampleFloat(frame, channel, bytesPerSample, isFloat);
                if ((channel & 1) == 0)
                {
                    left += sample;
                }
                else
                {
                    right += sample;
                }
            }
        }
        else
        {
            for (var channel = 2; channel < channels; channel++)
            {
                var sample = SurroundWeight * ReadSampleFloat(frame, channel, bytesPerSample, isFloat);
                left += sample;
                right += sample;
            }
        }
    }

    private static void WriteSample(Span<byte> destination, float value)
    {
        var clamped = Math.Clamp(value, -1.0f, 1.0f);
        BinaryPrimitives.WriteInt16LittleEndian(destination, (short)MathF.Round(clamped * short.MaxValue));
    }

    private static float ReadSampleFloat(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (isFloat)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
            return Math.Clamp(BitConverter.Int32BitsToSingle(bits), -1.0f, 1.0f);
        }

        return BinaryPrimitives.ReadInt16LittleEndian(sample) / (float)short.MaxValue;
    }
}
