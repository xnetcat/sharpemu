// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerAbiTests
{
    [Theory]
    [InlineData(Generation.Gen4, false, 108UL)]
    [InlineData(Generation.Gen5, false, 112UL)]
    [InlineData(Generation.Gen4, true, 164UL)]
    [InlineData(Generation.Gen5, true, 168UL)]
    public void InitAutoStartOffset_MatchesGeneration(
        Generation generation,
        bool extended,
        ulong expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetAutoStartOffset(generation, extended));
    }

    [Theory]
    [InlineData(Generation.Gen4, 40)]
    [InlineData(Generation.Gen5, 32)]
    public void LegacyStreamInfoSize_MatchesGeneration(Generation generation, int expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetLegacyStreamInfoSize(generation));
    }

    [Theory]
    [InlineData(Generation.Gen4, 0u, 0u)]
    [InlineData(Generation.Gen4, 1u, 1u)]
    [InlineData(Generation.Gen5, 0u, 1u)]
    [InlineData(Generation.Gen5, 1u, 2u)]
    public void StreamType_MatchesGeneration(
        Generation generation,
        uint streamIndex,
        uint expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetStreamType(generation, streamIndex));
    }

    [Fact]
    public void Gen5StreamInfoEx_WritesDurationAfterDetailsUnion()
    {
        var info = new byte[104];

        AvPlayerExports.WriteGen5StreamInfoEx(
            info,
            streamType: 1,
            width: 378,
            height: 150,
            framesPerSecond: 29.97,
            durationMilliseconds: 9_109);

        Assert.Equal(104UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8)));
        Assert.Equal(378U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16)));
        Assert.Equal(150U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20)));
        Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(info.AsSpan(0x40)));
        Assert.Equal(9_109UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(0x60)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(0x18)));
    }

    [Fact]
    public void Gen5FrameInfoEx_WritesPublishedVideoDetailsLayout()
    {
        var info = new byte[104];

        AvPlayerExports.WriteVideoFrameInfo(
            info,
            Generation.Gen5,
            extended: true,
            bufferAddress: 0x1234_5000,
            timestamp: 2_903,
            width: 512,
            visibleWidth: 378,
            height: 150,
            pitch: 512,
            framesPerSecond: 29.97);

        Assert.Equal(0x1234_5000UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(2_903UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(16)));
        Assert.Equal(512U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)));
        Assert.Equal(150U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28)));
        Assert.Equal(134U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48)));
        Assert.Equal(512U, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(60)));
        Assert.Equal(8, info[64]);
        Assert.Equal(8, info[65]);
        Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(info.AsSpan(0x48)));
    }

    [Theory]
    [InlineData(
        "file://../../../shpc/Content/Movies/ui_titlemovie01.mp4",
        "shpc/Content/Movies/ui_titlemovie01.mp4")]
    [InlineData(
        "app0:/shpc/Content/Movies/ui_titlemovie01.mp4",
        "shpc/Content/Movies/ui_titlemovie01.mp4")]
    [InlineData(
        "\\app0\\shpc\\Content\\Movies\\ui_titlemovie01.mp4",
        "shpc/Content/Movies/ui_titlemovie01.mp4")]
    [InlineData(
        "file:///Users/example/movie.mp4",
        "/Users/example/movie.mp4")]
    public void GuestMediaPath_IsAnchoredAtApp0(string input, string expected)
    {
        Assert.Equal(expected, AvPlayerExports.NormalizeGuestMediaPath(input));
    }

    [Theory]
    [InlineData(9_109UL, 9_076UL, 29.97, 9_108UL, false)]
    [InlineData(9_109UL, 9_076UL, 29.97, 9_109UL, true)]
    [InlineData(9_109UL, 8_000UL, 29.97, 12_000UL, false)]
    [InlineData(9_109UL, 9_109UL, 29.97, 9_109UL, true)]
    [InlineData(0UL, 0UL, 29.97, 12_000UL, false)]
    [InlineData(9_109UL, 9_076UL, 0.0, 12_000UL, false)]
    public void PlaybackEnd_RequiresElapsedDurationAndFinalFrame(
        ulong durationMilliseconds,
        ulong lastVideoTimestamp,
        double framesPerSecond,
        ulong elapsedMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.HasPlaybackReachedEnd(
                durationMilliseconds,
                lastVideoTimestamp,
                framesPerSecond,
                elapsedMilliseconds));
    }
}
