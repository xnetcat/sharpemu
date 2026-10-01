// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal static class VertexReplayIndices
{
    // Each emitted corner fetches the same three guest indices plus its own corner
    // number. Restart splits strips/fans before applying the signed base vertex.
    internal static int MaximumByteCount(int indexCount, PrimitiveTopology topology) => checked(
        (topology switch
        {
            PrimitiveTopology.TriangleList => indexCount / 3,
            PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan => Math.Max(0, indexCount - 2),
            _ => throw new NotSupportedException($"Per-vertex replay does not support {topology}."),
        }) * 48);

    internal static byte[] Build(ReadOnlySpan<uint> indices, PrimitiveTopology topology, uint? restart, int baseVertex)
    {
        var data = new byte[MaximumByteCount(indices.Length, topology)];
        var written = Write(indices, topology, restart, baseVertex, data);
        return written == data.Length ? data : data.AsSpan(0, written).ToArray();
    }

    internal static int Write(ReadOnlySpan<uint> indices, PrimitiveTopology topology, uint? restart, int baseVertex, Span<byte> data)
    {
        if (data.Length < MaximumByteCount(indices.Length, topology))
            throw new ArgumentException("The vertex replay destination is too small.", nameof(data));
        uint first = 0, previous = 0;
        var count = 0;
        var offset = 0;
        foreach (var index in indices)
        {
            if (restart == index) { count = 0; continue; }
            if (count == 0) first = index;
            else if (count == 1) previous = index;
            else
            {
                var b = previous;
                var c = index;
                if (topology == PrimitiveTopology.TriangleStrip && (count & 1) != 0)
                    (b, c) = (c, b);
                for (uint corner = 0; corner < 3; corner++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], unchecked(first + (uint)baseVertex));
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(offset + 4)..], unchecked(b + (uint)baseVertex));
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(offset + 8)..], unchecked(c + (uint)baseVertex));
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(offset + 12)..], corner);
                    offset += 16;
                }
                if (topology == PrimitiveTopology.TriangleList) count = -1;
                else
                {
                    if (topology == PrimitiveTopology.TriangleStrip) first = previous;
                    previous = index;
                }
            }
            count++;
        }
        return offset;
    }
}
