// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// Emulated NGG geometry stage. Hosts without geometry shaders (Metal) cannot run the merged
// export and geometry program as a pipeline stage, so each draw runs it as a compute pass: one
// 64-lane workgroup per subgroup of whole input primitives, with the system registers of that
// subgroup built here. The pass stores each lane's primitive and vertex exports to a record
// buffer, and a replay vertex program draws the exported triangles with the bound pixel stage.
public sealed partial class RenderExecutor
{
    private const uint GeometryStageEnableBit = 1u << 5;
    private const uint PrimitiveGenerationBit = 1u << 13;
    private const uint TessellationStagesMask = 0x7u;
    private const uint GeometryOutputTriangleStrip = 2;
    private const uint NggVerticesPerPrimitive = 3;
    private const uint MaxNggSubgroups = 1u << 16;

    private readonly record struct NggReplay(uint ParamCount, ulong Input, ulong Output);

    private NggReplay? _nggReplay;

    private static readonly bool LogGeometryResources = Environment.GetEnvironmentVariable("SHARPEMU_LOG_GEOMETRY_RESOURCES") == "1";
    private readonly HashSet<ulong> _loggedGeometryPrograms = new();

    private static readonly string? NggSkip = Environment.GetEnvironmentVariable("SHARPEMU_NGG_SKIP");

    private static readonly bool DisableGeometryEmulation =
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_GEOMETRY_EMULATION") == "1";

    private static bool IsEmulatableGeometryStage(RegisterBanks banks)
    {
        var stages = banks.Context.ShaderStages;
        return !DisableGeometryEmulation &&
            (stages & GeometryStageEnableBit) != 0 &&
            (stages & PrimitiveGenerationBit) != 0 &&
            (stages & TessellationStagesMask) == 0 &&
            banks.Shader.Vertex.ExportAddress != 0;
    }

    // Runs the geometry program over the draw's primitives and replays its output; false when
    // the draw cannot be emulated, after reporting it.
    private bool TryEmulateGeometryStage(
        ulong submitId,
        RegisterBanks banks,
        ulong packetAddress,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        ulong indexAddress,
        uint indexTypeAndSize,
        int baseVertex,
        bool indexed)
    {
        var primitiveType = (GuestPrimitiveType)banks.UserConfig.PrimitiveType;
        var verticesPerPrimitive = primitiveType switch
        {
            GuestPrimitiveType.PointList => 1u,
            GuestPrimitiveType.LineList or GuestPrimitiveType.LineStrip => 2u,
            GuestPrimitiveType.TriangleList or GuestPrimitiveType.TriangleStrip => 3u,
            _ => 0u,
        };
        if (verticesPerPrimitive == 0)
        {
            DroppedWorkLog.Draw($"geometry-stage-input-topology-{primitiveType}", banks);
            return false;
        }

        // The replay rasterizes the exported primitives as triangles.
        var shaderInterface = banks.Context.ShaderInterface;
        if (shaderInterface.GeometryMaxVerticesOut != 0 && shaderInterface.GeometryOutputPrimitiveType != GeometryOutputTriangleStrip)
        {
            DroppedWorkLog.Draw($"geometry-stage-output-topology-{shaderInterface.GeometryOutputPrimitiveType}", banks);
            return false;
        }

        var primitivesPerSubgroup = PrimitivesPerSubgroup(shaderInterface, verticesPerPrimitive);

        var vertexIds = new uint[vertexCount];
        if (indexed)
        {
            var indexBytes = (GuestIndexType)indexTypeAndSize switch
            {
                GuestIndexType.Index16 => 2,
                GuestIndexType.Index32 => 4,
                _ => 1,
            };
            var indices = new byte[checked((int)vertexCount * indexBytes)];
            if (indexAddress == 0 || !_host.TryReadGuest(indexAddress, indices))
            {
                DroppedWorkLog.Draw("geometry-stage-index-read", banks);
                return false;
            }

            for (var vertex = 0; vertex < vertexIds.Length; vertex++)
            {
                var index = indexBytes switch
                {
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(vertex * 2)),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(indices.AsSpan(vertex * 4)),
                    _ => indices[vertex],
                };
                vertexIds[vertex] = unchecked((uint)((int)index + baseVertex));
            }
        }
        else
        {
            for (var vertex = 0u; vertex < vertexCount; vertex++)
            {
                vertexIds[vertex] = firstVertex + vertex;
            }
        }

        var strip = primitiveType is GuestPrimitiveType.TriangleStrip or GuestPrimitiveType.LineStrip;
        var primitivesPerInstance = strip
            ? (vertexCount >= verticesPerPrimitive ? vertexCount - (verticesPerPrimitive - 1) : 0)
            : vertexCount / verticesPerPrimitive;
        var subgroupsPerInstance = (primitivesPerInstance + primitivesPerSubgroup - 1) / primitivesPerSubgroup;
        var subgroupCount = (ulong)subgroupsPerInstance * instanceCount;
        if (primitivesPerInstance == 0 || subgroupCount == 0)
        {
            return true;
        }

        if (subgroupCount > MaxNggSubgroups)
        {
            DroppedWorkLog.Draw("geometry-stage-too-large", banks);
            return false;
        }

        if (SharpEmu.Libs.Gpu.GpuCommands.Registers.RegisterWriters.LogUserDataEnabled)
        {
            var gs = banks.Shader.Vertex.GeometryUserScalars;
            Console.Error.WriteLine(
                $"[GPU][USERDATA] NGG dispatch shader=0x{banks.Shader.Vertex.ExportAddress:X16} " +
                $"declared={banks.Shader.Vertex.GeometryResource2.UserScalarCount} count={gs.Count} " +
                $"s36:s37=0x{((ulong)gs.Values[29] << 32) | gs.Values[28]:X16} " +
                $"s38:s39=0x{((ulong)gs.Values[31] << 32) | gs.Values[30]:X16}");
        }

        var compute = _pipelines.GetNggComputeProgram(banks.Shader.Vertex, out var paramCount);
        if (!compute.Available)
        {
            DroppedWorkLog.Draw("geometry-stage-program", banks);
            return false;
        }

        var input = BuildNggInput(
            AssemblePrimitiveVertices(vertexIds, primitivesPerInstance, strip, verticesPerPrimitive),
            primitivesPerInstance, subgroupsPerInstance, instanceCount, verticesPerPrimitive, primitivesPerSubgroup);
        var recordBytes = subgroupCount * NggRecordLayout.WaveLanes * NggRecordLayout.RecordDwords(paramCount) * sizeof(uint);
        var inputAddress = _host.CreateTransientDeviceBuffer(input, (ulong)input.Length * sizeof(uint));
        var outputAddress = inputAddress == 0 ? 0 : _host.CreateTransientDeviceBuffer([], recordBytes);
        if (outputAddress == 0)
        {
            _host.ReleaseTransientDeviceBuffers();
            DroppedWorkLog.Draw("geometry-stage-host-buffers", banks);
            return false;
        }

        try
        {
            RecordGeometryStage(submitId, banks, packetAddress, compute, paramCount, subgroupCount, primitivesPerInstance, instanceCount, inputAddress, outputAddress);
        }
        finally
        {
            // After the replay draw: the batch recording now is the last to read the records.
            _host.ReleaseTransientDeviceBuffers();
        }

        return true;
    }

    private void RecordGeometryStage(
        ulong submitId,
        RegisterBanks banks,
        ulong packetAddress,
        ComputeProgram compute,
        uint paramCount,
        ulong subgroupCount,
        uint primitivesPerInstance,
        uint instanceCount,
        ulong inputAddress,
        ulong outputAddress)
    {

        var computeInput = compute.Input;
        if (LogGeometryResources && _loggedGeometryPrograms.Add(computeInput.Stage.Program!.Hash))
        {
            var program = computeInput.Stage.Program!;
            var descriptors = computeInput.Stage.Resources.Buffers;
            for (var index = 0; index < program.Buffers.Length && index < descriptors.Length; index++)
            {
                var info = program.Buffers[index];
                var words = descriptors[index];
                var descriptor = words.Length >= 4 ? BufferDescriptorWords.From(words) : default;
                Console.Error.WriteLine(
                    $"[GPU][INFO] Geometry program 0x{program.Hash:X16} buffer {index}: address=0x{descriptor.Address:X} stride={descriptor.Stride} " +
                    $"records={descriptor.RecordCount} words={words.Length} read={info.Read} written={info.Written} scalar={info.Scalar} " +
                    $"formatted={info.Formatted} extent={info.MaxByteExtent} packedStride={info.PackedStride} " +
                    $"descriptor=[{string.Join(' ', words.Select(static word => $"{word:X8}"))}]");
            }
        }

        if (LogGeometryResources)
        {
            Console.Error.WriteLine(
                $"[GPU][INFO] Emulated geometry stage: program=0x{computeInput.Stage.Program!.Hash:X16} export=0x{banks.Shader.Vertex.ExportAddress:X} " +
                $"subgroups={subgroupCount} primitives={primitivesPerInstance}x{instanceCount} params={paramCount} pixel=0x{banks.Shader.Pixel.Address:X}");
        }

        computeInput.Stage = computeInput.Stage with { NggBuffers = (inputAddress, outputAddress) };
        _host.EndRendering();
        if (NggSkip != "compute")
        using (_host.BeginPreparation())
        {
            var pipeline = _pipelines.CreateComputePipeline(computeInput, compute.Program);
            var bindings = _host.PrepareBindings(computeInput.Stage);
            if (computeInput.Stage.Program!.UsesDeviceAddresses)
            {
                _host.PrepareDeviceAddresses();
            }

            _host.BindResources(bindings);
            Span<IPreparedBindings> stages = [bindings];
            _host.CommitBindings(PipelineBindPoint.Compute, in pipeline, stages);
            _host.ShaderWriteHazardBarrier();
            _host.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            GpuWorkTrace.Note($"ngg compute cs=0x{computeInput.Stage.Program!.Hash:X16} subgroups={subgroupCount}");
            _host.Dispatch((uint)subgroupCount, 1, 1);
            _host.ShaderAccessBarrier();
        }

        _host.ResetBindings();
        if (FrameCommandLog.Active)
        {
            FrameCommandLog.Write(
                $"  emulated geometry stage: subgroups={subgroupCount} primitives={primitivesPerInstance}x{instanceCount} params={paramCount}");
        }

        if (NggSkip == "draw")
        {
            return;
        }

        _nggReplay = new NggReplay(paramCount, inputAddress, outputAddress);
        try
        {
            var replayVertices = checked((uint)(subgroupCount * NggRecordLayout.WaveLanes * NggVerticesPerPrimitive));
            DrawAuto(submitId, banks, new DrawAutoArguments(packetAddress, PacketOpcode.DrawIndexAuto, replayVertices, 1, 0, 0, DrawOffsetSource.Packet));
        }
        finally
        {
            _nggReplay = null;
        }
    }

    // The vertex ids of each input primitive, in order; strip triangles alternate winding.
    private static uint[] AssemblePrimitiveVertices(uint[] vertexIds, uint primitives, bool strip, uint verticesPerPrimitive)
    {
        if (!strip)
        {
            return vertexIds;
        }

        var assembled = new uint[primitives * verticesPerPrimitive];
        for (var primitive = 0u; primitive < primitives; primitive++)
        {
            if (verticesPerPrimitive == 2)
            {
                assembled[primitive * 2] = vertexIds[primitive];
                assembled[primitive * 2 + 1] = vertexIds[primitive + 1];
                continue;
            }

            var odd = (primitive & 1) != 0;
            assembled[primitive * 3] = vertexIds[primitive + (odd ? 1u : 0u)];
            assembled[primitive * 3 + 1] = vertexIds[primitive + (odd ? 0u : 1u)];
            assembled[primitive * 3 + 2] = vertexIds[primitive + 2];
        }

        return assembled;
    }

    // Whole input primitives per emulated 64-lane subgroup: no more than the title configured
    // (VGT_GS_ONCHIP_CNTL), and few enough that every input vertex and every output vertex the
    // geometry program may allocate (VGT_GS_MAX_VERT_OUT per primitive) gets its own lane.
    private static uint PrimitivesPerSubgroup(ShaderInterfaceRegisters shaderInterface, uint verticesPerPrimitive)
    {
        var primitives = NggRecordLayout.WaveLanes / verticesPerPrimitive;
        var maxVerticesOut = shaderInterface.GeometryMaxVerticesOut;
        if (maxVerticesOut is > 0 and <= NggRecordLayout.WaveLanes)
        {
            primitives = Math.Min(primitives, NggRecordLayout.WaveLanes / maxVerticesOut);
        }

        var configured = shaderInterface.GeometryPrimitivesPerSubgroup;
        if (configured != 0)
        {
            primitives = Math.Min(primitives, configured);
        }

        return Math.Max(1u, primitives);
    }

    // Per subgroup: gs_tg_info and merged_wave_info, then v0..v8 of each lane. A lane is both
    // export vertex (vertex id in v5, instance id in v8) and geometry primitive (its three
    // vertex slots, each shifted left by two, in v0 and v1; the primitive id in v2).
    private static uint[] BuildNggInput(
        uint[] vertexIds,
        uint primitivesPerInstance,
        uint subgroupsPerInstance,
        uint instanceCount,
        uint verticesPerPrimitive,
        uint primitivesPerSubgroup)
    {
        var input = new uint[checked((int)((ulong)subgroupsPerInstance * instanceCount * NggRecordLayout.InputGroupDwords))];
        var group = 0;
        for (var instance = 0u; instance < instanceCount; instance++)
        {
            for (var subgroup = 0u; subgroup < subgroupsPerInstance; subgroup++, group++)
            {
                var firstPrimitive = subgroup * primitivesPerSubgroup;
                var primitives = Math.Min(primitivesPerSubgroup, primitivesPerInstance - firstPrimitive);
                var vertices = primitives * verticesPerPrimitive;
                var header = group * (int)NggRecordLayout.InputGroupDwords;
                input[header] = (vertices << 12) | (primitives << 22);
                input[header + 1] = vertices | (primitives << 8) | (1u << 28);
                for (var lane = 0u; lane < NggRecordLayout.WaveLanes; lane++)
                {
                    var registers = header + (int)(NggRecordLayout.InputHeaderDwords + lane * NggRecordLayout.InputLaneDwords);
                    if (lane < vertices)
                    {
                        input[registers + 5] = vertexIds[firstPrimitive * verticesPerPrimitive + lane];
                        input[registers + 8] = instance;
                    }

                    if (lane < primitives)
                    {
                        var slot = lane * verticesPerPrimitive;
                        input[registers] = (slot << 2) |
                            (verticesPerPrimitive > 1 ? (slot + 1) << 18 : 0);
                        input[registers + 1] = verticesPerPrimitive > 2 ? (slot + 2) << 2 : 0;
                        input[registers + 2] = firstPrimitive + lane;
                    }
                }
            }
        }

        return input;
    }
}
