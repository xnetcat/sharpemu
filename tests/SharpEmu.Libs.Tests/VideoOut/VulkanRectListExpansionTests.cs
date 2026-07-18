// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRectListExpansionTests
{
    [Fact]
    public void InterleavedFloatAttributes_ProduceFourthCornerAndTriangleIndices()
    {
        var data = new byte[3 * 16];
        WriteFloat2(data, 0, 0, 0);
        WriteFloat2(data, 8, 0, 0);
        WriteFloat2(data, 16, 100, 0);
        WriteFloat2(data, 24, 1, 0);
        WriteFloat2(data, 32, 0, 50);
        WriteFloat2(data, 40, 0, 1);
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 11, 7, 16, 0, data),
            CreateBuffer(1, 2, 11, 7, 16, 8, data),
        ];

        var expanded = VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out var expandedBuffers,
            out var indices,
            out var index32Bit,
            out var indexCount);

        Assert.True(expanded);
        Assert.False(index32Bit);
        Assert.Equal(6u, indexCount);
        Assert.Same(expandedBuffers[0].Data, expandedBuffers[1].Data);
        Assert.Equal((100f, 50f), ReadFloat2(expandedBuffers[0].Data, 48));
        Assert.Equal((1f, 1f), ReadFloat2(expandedBuffers[1].Data, 56));
        Assert.Equal(
            new ushort[] { 0, 1, 2, 2, 1, 3 },
            ReadUInt16Indices(indices));
    }

    [Fact]
    public void CornerMayBeSecondControlVertex()
    {
        var data = new byte[3 * 8];
        WriteFloat2(data, 0, 100, 0);
        WriteFloat2(data, 8, 0, 0);
        WriteFloat2(data, 16, 0, 50);
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 11, 7, 8, 0, data),
        ];

        Assert.True(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out var expandedBuffers,
            out var indices,
            out _,
            out _));

        Assert.Equal((100f, 50f), ReadFloat2(expandedBuffers[0].Data, 24));
        Assert.Equal(
            new ushort[] { 1, 2, 0, 0, 2, 3 },
            ReadUInt16Indices(indices));
    }

    [Fact]
    public void HalfFloatAndConstantPackedColor_AreExpanded()
    {
        var positions = new byte[3 * 4];
        WriteHalf2(positions, 0, (Half)0f, (Half)0f);
        WriteHalf2(positions, 4, (Half)2f, (Half)0f);
        WriteHalf2(positions, 8, (Half)0f, (Half)3f);
        var colors = new byte[]
        {
            0x11, 0x22, 0x33, 0x44,
            0x11, 0x22, 0x33, 0x44,
            0x11, 0x22, 0x33, 0x44,
        };
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 5, 7, 4, 0, positions),
            CreateBuffer(1, 4, 10, 0, 4, 0, colors),
        ];

        Assert.True(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out var expandedBuffers,
            out _,
            out _,
            out _));

        Assert.Equal((2f, 3f), ReadHalf2(expandedBuffers[0].Data, 12));
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
            expandedBuffers[1].Data.AsSpan(12, 4).ToArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void InvalidControlCount_IsRejected(uint vertexCount)
    {
        var data = new byte[24];
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 11, 7, 8, 0, data),
        ];

        Assert.False(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            vertexCount,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void ShortPositionStream_IsRejected()
    {
        var data = new byte[16];
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 11, 7, 8, 0, data),
        ];

        Assert.False(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void NegativeCapturedLength_IsRejected()
    {
        var data = new byte[24];
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 2, 11, 7, 8, 0, data) with { Length = -1 },
        ];

        Assert.False(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void InvalidComponentCount_IsRejected()
    {
        var data = new byte[48];
        GuestVertexBuffer[] buffers =
        [
            CreateBuffer(0, 5, 99, 7, 16, 0, data),
        ];

        Assert.False(VulkanVideoPresenter.TryExpandRectListVertexBuffers(
            buffers,
            3,
            out _,
            out _,
            out _,
            out _));
    }

    private static GuestVertexBuffer CreateBuffer(
        uint location,
        uint componentCount,
        uint dataFormat,
        uint numberFormat,
        uint stride,
        uint offset,
        byte[] data) =>
        new(
            location,
            componentCount,
            dataFormat,
            numberFormat,
            BaseAddress: 0,
            stride,
            offset,
            data,
            data.Length,
            Pooled: false);

    private static void WriteFloat2(
        byte[] destination,
        int offset,
        float x,
        float y)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset + 4, 4),
            BitConverter.SingleToInt32Bits(y));
    }

    private static (float X, float Y) ReadFloat2(byte[] source, int offset) =>
        (
            BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, 4))),
            BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset + 4, 4))));

    private static void WriteHalf2(
        byte[] destination,
        int offset,
        Half x,
        Half y)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.AsSpan(offset, 2),
            BitConverter.HalfToUInt16Bits(x));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.AsSpan(offset + 2, 2),
            BitConverter.HalfToUInt16Bits(y));
    }

    private static (float X, float Y) ReadHalf2(byte[] source, int offset) =>
        (
            (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, 2))),
            (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset + 2, 2))));

    private static ushort[] ReadUInt16Indices(byte[] source)
    {
        var indices = new ushort[source.Length / 2];
        for (var index = 0; index < indices.Length; index++)
        {
            indices[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                source.AsSpan(index * 2, 2));
        }

        return indices;
    }
}
