// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal static class VertexReplayIndices
{
    // Each emitted corner fetches the same three guest indices plus its own corner
    // number. Restart splits strips/fans before applying the signed base vertex.
    internal static byte[] Build(ReadOnlySpan<uint> indices, PrimitiveTopology topology, uint? restart, int baseVertex)
    {
        if (topology is not (PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan))
            throw new NotSupportedException($"Per-vertex replay does not support {topology}.");
        var triangles = new List<(uint A, uint B, uint C)>();
        uint first = 0, previous = 0;
        var count = 0;
        foreach (var index in indices)
        {
            if (restart == index) { count = 0; continue; }
            if (count == 0) first = index;
            else if (count == 1) previous = index;
            else
            {
                if (topology == PrimitiveTopology.TriangleList)
                {
                    triangles.Add((first, previous, index));
                    count = -1;
                }
                else if (topology == PrimitiveTopology.TriangleFan)
                {
                    triangles.Add((first, previous, index));
                    previous = index;
                }
                else
                {
                    triangles.Add((first, (count & 1) == 0 ? previous : index, (count & 1) == 0 ? index : previous));
                    first = previous;
                    previous = index;
                }
            }
            count++;
        }
        var data = new byte[checked(triangles.Count * 3 * 16)];
        var offset = 0;
        foreach (var (a, b, c) in triangles)
        {
            for (uint corner = 0; corner < 3; corner++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), unchecked(a + (uint)baseVertex));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), unchecked(b + (uint)baseVertex));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8), unchecked(c + (uint)baseVertex));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 12), corner);
                offset += 16;
            }
        }
        return data;
    }
}
