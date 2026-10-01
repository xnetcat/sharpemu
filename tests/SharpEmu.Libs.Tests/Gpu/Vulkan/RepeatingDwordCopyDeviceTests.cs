// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class RepeatingDwordCopyDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(1u, 152u, 192u)]
    [InlineData(3u, 152u, 192u)]
    [InlineData(7u, 151u, 192u)]
    [InlineData(17u, 129u, 192u)]
    [InlineData(257u, 152u, 192u)]
    [InlineData(uint.MaxValue, 152u, 192u)]
    [InlineData(3u, 152u, 130u)]
    [InlineData(7u, 151u, 63u)]
    public void CpuReplacementMatchesOriginalShader(uint period, uint count, uint threadLimit)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = RepeatingDwordCopyTests.DecodeKernel();
        var input = Enumerable.Range(0, 257).Select(i => unchecked((uint)i * 0x9E3779B9u + 17)).ToArray();
        var initial = Enumerable.Repeat(0xCDCDCDCDu, 192).ToArray();
        uint[] user = [0x10000, 4u << 16, (uint)input.Length, 0x14004,
            0x20000, 4u << 16, (uint)initial.Length, 0x14004,
            0x30000, 16u << 16, 1, 0x4DFAC];
        var plan = ResourceTestProgram.Extract(program, userDataCount: 12, waveSize: 32);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(user), ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = threadLimit, ThreadCountY = 1, ThreadCountZ = 1,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var source = runner.CreateBuffer(MemoryMarshal.AsBytes(input.AsSpan()));
        var destination = runner.CreateBuffer(MemoryMarshal.AsBytes(initial.AsSpan()));
        var control = runner.CreateBuffer(MemoryMarshal.AsBytes(new uint[] { count, period, 0, 0 }.AsSpan()));
        var buffers = snapshot.Buffers.Select(words => words[0] switch
        {
            0x10000 => source, 0x20000 => destination, 0x30000 => control,
            _ => throw new InvalidOperationException("Unexpected kernel descriptor"),
        }).ToArray();
        harness.Run(() => runner.Dispatch(user, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, 3,
            flattenedTable: snapshot.FlattenedResourceTable));
        var actual = runner.ReadBack(destination, 0, (ulong)initial.Length * 4);
        var memory = new FakeCpuMemory(0x10000, 0x11000);
        Assert.True(memory.TryWrite(0x10000, MemoryMarshal.AsBytes(input.AsSpan())));
        Assert.True(memory.TryWrite(0x20000, MemoryMarshal.AsBytes(initial.AsSpan())));
        Assert.True(ShaderPipelineCache.TryCopyRepeatingDwords(memory, 0x10000, input.Length * 4, 0x20000, Math.Min(count, threadLimit), period));
        var expected = new byte[actual.Length];
        Assert.True(memory.TryRead(0x20000, expected));
        Assert.Equal(expected, actual);
        harness.AssertNoValidationMessages();
    }
}
