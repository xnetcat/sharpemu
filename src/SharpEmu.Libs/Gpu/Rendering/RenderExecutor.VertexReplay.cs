// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
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
        var indexCount = checked((int)draw.Count);
        var rentedIndices = ArrayPool<uint>.Shared.Rent(indexCount);
        byte[]? rentedSource = null;
        byte[]? rentedPayload = null;
        try
        {
            var indices = rentedIndices.AsSpan(0, indexCount);
            if (source.Enabled)
            {
                ReadOnlySpan<byte> bytes;
                if (source.HostData is { } hostData) bytes = hostData;
                else
                {
                    var sourceSize = checked((int)source.Size);
                    rentedSource = ArrayPool<byte>.Shared.Rent(sourceSize);
                    var destination = rentedSource.AsSpan(0, sourceSize);
                    if (!_host.TryReadGuest(source.Address, destination))
                        throw _host.Fatal("Per-vertex replay cannot read the index buffer.");
                    bytes = destination;
                }
                for (var i = 0; i < indices.Length; i++)
                    indices[i] = source.Type == IndexType.Uint16
                        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
            }
            else
            {
                for (var i = 0; i < indices.Length; i++) indices[i] = unchecked(emission.FirstVertex + (uint)i);
            }
            var capacity = VertexReplayIndices.MaximumByteCount(indexCount, topology);
            rentedPayload = ArrayPool<byte>.Shared.Rent(capacity);
            var byteCount = VertexReplayIndices.Write(indices, topology,
                restart ? source.Type == IndexType.Uint16 ? ushort.MaxValue : uint.MaxValue : null,
                source.Enabled ? emission.VertexOffset : 0, rentedPayload.AsSpan(0, capacity));
            var payload = rentedPayload.AsSpan(0, byteCount);
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
        finally
        {
            ArrayPool<uint>.Shared.Return(rentedIndices);
            if (rentedSource is not null) ArrayPool<byte>.Shared.Return(rentedSource);
            // UploadTransient synchronously copies into storage owned until GPU completion.
            if (rentedPayload is not null) ArrayPool<byte>.Shared.Return(rentedPayload);
        }
    }
}
