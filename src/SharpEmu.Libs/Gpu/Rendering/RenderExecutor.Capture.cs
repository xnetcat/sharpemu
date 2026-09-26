// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// The capture seam of the executor: one case per matched draw or dispatch, written from the same
// state the host is about to record with.
public sealed partial class RenderExecutor
{
    // Opens a case for a draw whose vertex or pixel program matches a selector.
    private WorkCaptureBuilder? BeginDrawCapture(
        ulong submitId,
        RegisterBanks banks,
        in DrawCall draw,
        ref DrawState state,
        PrimitiveTopology topology,
        in DrawEmission emission,
        in IndexSource indexSource,
        bool primitiveRestart)
    {
        var shader = banks.Shader;
        var vertex = state.Programs.VertexInput.Stage;
        var pixel = state.Programs.PixelInput.Stage;
        var builder = WorkCapture.TryBegin(
            emission.Indexed ? WorkCaseKind.DrawIndexed : WorkCaseKind.DrawAuto,
            submitId,
            _host.TryReadGuest,
            vertex.Program?.Hash ?? 0,
            state.PixelActive ? pixel.Program?.Hash ?? 0 : 0,
            shader.Vertex.ExportAddress,
            shader.Pixel.Address);
        if (builder is null)
        {
            return null;
        }

        builder.SetBanks(banks);
        builder.Manifest.Draw = new WorkCaseDraw
        {
            Indexed = emission.Indexed,
            Count = draw.Count,
            InstanceCount = draw.InstanceCount,
            FirstVertex = emission.FirstVertex,
            FirstInstance = emission.FirstInstance,
            BaseVertex = emission.VertexOffset,
            IndexAddress = indexSource.Address,
            IndexTypeAndSize = banks.IndexTypeAndSize,
            PrimitiveType = banks.UserConfig.PrimitiveType,
            Topology = topology.ToString(),
            PrimitiveRestart = primitiveRestart,
        };
        builder.AddStage(vertex, shader.Vertex.ExportAddress);
        RecordWrittenBuffers(builder, vertex);
        if (state.PixelActive)
        {
            builder.AddStage(pixel, shader.Pixel.Address);
            RecordWrittenBuffers(builder, pixel);
        }

        foreach (ref readonly var buffer in state.Programs.VertexInput.Buffers.AsSpan())
        {
            builder.AddRange("vertex-fetch", buffer.Address, buffer.Size);
        }

        if (indexSource.Enabled)
        {
            builder.AddRange("index-buffer", indexSource.Address, indexSource.Size);
            if (indexSource.HostData is not null)
            {
                builder.Note("the index data was rewritten on the host before the draw; the case carries the guest bytes only");
            }
        }

        foreach (ref readonly var color in BoundColors(ref state))
        {
            var resolution = color.Resolution;
            builder.Manifest.Targets.Add(new WorkCaseTarget
            {
                Role = $"color{color.Slot}",
                Slot = color.Slot,
                Address = resolution.BaseAddress,
                Width = resolution.Extent.Width,
                Height = resolution.Extent.Height,
                Samples = resolution.Samples,
                Format = resolution.Request.Description.PixelFormat.ToString(),
            });
            builder.AddRange($"color{color.Slot}", resolution.BaseAddress, resolution.BackingSize);
            builder.CaptureImage("before", $"color{color.Slot}", resolution.BaseAddress);
        }

        if (state.Depth.HasTarget)
        {
            var depth = state.Depth.Target.Target;
            builder.Manifest.Targets.Add(new WorkCaseTarget
            {
                Role = "depth",
                Address = depth.DepthAddress,
                Width = depth.Width,
                Height = depth.Height,
                Samples = depth.Samples,
                Format = depth.Format.ToString(),
            });
            builder.AddRange("depth", depth.DepthAddress, depth.DepthSize);
            builder.AddRange("stencil", depth.StencilAddress, depth.StencilSize);
            builder.AddRange("htile", depth.HtileAddress, depth.HtileSize);
            builder.CaptureImage("before", "depth", depth.DepthAddress);
        }

        return builder;
    }

    // Opens a case for a dispatch whose compute program matches a selector.
    private WorkCaptureBuilder? BeginDispatchCapture(
        ulong submitId,
        RegisterBanks banks,
        ComputeInputInfo input,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        uint dispatchInitiator,
        ulong indirectArgumentsAddress)
    {
        var compute = banks.Shader.Compute;
        var builder = WorkCapture.TryBegin(
            WorkCaseKind.Dispatch, submitId, _host.TryReadGuest, input.Stage.Program?.Hash ?? 0, compute.Address);
        if (builder is null)
        {
            return null;
        }

        builder.SetBanks(banks);
        builder.Manifest.Dispatch = new WorkCaseDispatch
        {
            GroupsX = groupsX,
            GroupsY = groupsY,
            GroupsZ = groupsZ,
            Initiator = dispatchInitiator,
            IndirectArgumentsAddress = indirectArgumentsAddress,
            ThreadsX = input.ThreadsX,
            ThreadsY = input.ThreadsY,
            ThreadsZ = input.ThreadsZ,
        };
        builder.AddStage(input.Stage, compute.Address);
        RecordWrittenBuffers(builder, input.Stage);
        if (indirectArgumentsAddress != 0)
        {
            builder.AddRange("indirect-arguments", indirectArgumentsAddress, 3 * sizeof(uint));
        }

        return builder;
    }

    // The buffers a stage writes, so a replay can dump exactly those ranges.
    private static void RecordWrittenBuffers(WorkCaptureBuilder builder, ShaderStageResources stage)
    {
        if (stage.Program is not { } program)
        {
            return;
        }

        var descriptors = stage.Resources.Buffers;
        for (var index = 0; index < program.Buffers.Length && index < descriptors.Length; index++)
        {
            if (!program.Buffers[index].Written || descriptors[index].Length < 4)
            {
                continue;
            }

            var descriptor = BufferDescriptorWords.From(descriptors[index]);
            builder.Manifest.WrittenBuffers.Add(new WorkCaseRange
            {
                Role = $"{program.Stage.ToString().ToLowerInvariant()}-buffer{index}",
                Address = descriptor.Address,
                Size = descriptor.Footprint() ?? 0,
            });
        }
    }

    // Downloads what the work wrote, then writes the case.
    private static void FinishCapture(WorkCaptureBuilder builder)
    {
        foreach (var target in builder.Manifest.Targets.ToArray())
        {
            builder.CaptureImage("after", target.Role, target.Address);
        }

        builder.Complete();
    }
}
