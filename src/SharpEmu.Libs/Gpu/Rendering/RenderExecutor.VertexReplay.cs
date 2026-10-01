// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    private BufferBinding PrepareVertexReplay(VertexInputInfo original, in DrawCall draw, in DrawEmission emission,
        in IndexSource source, PrimitiveTopology topology, bool restart, out VertexInputInfo input, out uint count)
    {
        if (emission.IndirectArgumentsAddress != 0)
            throw _host.Fatal("Per-vertex replay requires resolved indirect draw arguments.");
        var indices = new uint[checked((int)draw.Count)];
        if (source.Enabled)
        {
            var bytes = source.HostData ?? new byte[checked((int)source.Size)];
            if (source.HostData is null && !_host.TryReadGuest(source.Address, bytes))
                throw _host.Fatal("Per-vertex replay cannot read the index buffer.");
            for (var i = 0; i < indices.Length; i++)
                indices[i] = source.Type == IndexType.Uint16
                    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2))
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }
        else
        {
            for (var i = 0; i < indices.Length; i++) indices[i] = unchecked(emission.FirstVertex + (uint)i);
        }
        var payload = VertexReplayIndices.Build(indices, topology,
            restart ? source.Type == IndexType.Uint16 ? ushort.MaxValue : uint.MaxValue : null,
            source.Enabled ? emission.VertexOffset : 0);
        count = (uint)(payload.Length / 16);
        // Exact unsigned values, never normalized or interpolated on the way in.
        var descriptor = new BufferDescriptorWords(0, 16u << 16, count,
            (BufferDescriptorWords.Format32x4UInt << 12) | 4u | (5u << 3) | (6u << 6) | (7u << 9));
        input = new VertexInputInfo
        {
            Stage = original.Stage,
            Buffers = [new VertexInputBuffer(0, 16, count)],
            Attributes = [new VertexAttributeResource(descriptor, 0, 4, 0, 0, 0, 0)],
            ReplayParameters = original.ReplayParameters,
            ClipSpace = original.ClipSpace,
            PositionExportControl = original.PositionExportControl,
        };
        return payload.Length == 0 ? _host.NullBuffer : _host.UploadTransient(payload, 16);
    }
}
