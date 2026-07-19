// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderDescriptorProvenanceTests
{
    [Fact]
    public void ImageBindingRetainsDirectScalarLoadAddress()
    {
        var memory = new FakeCpuMemory(0x1000, 0x3000);
        var state = CreateState(
            [0x2000, 0],
            userDataSources: null);

        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            new CpuContext(memory, Generation.Gen5),
            state,
            out var evaluation,
            out var error), error);

        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(0x2000UL, binding.DescriptorSourceAddress);
        Assert.Null(binding.DeferredChain);
    }

    [Fact]
    public void ImageBindingRetainsLateWrittenPointerChain()
    {
        var memory = new FakeCpuMemory(0x1000, 0x3000);
        var state = CreateState(
            [0, 0],
            [0x1100, 0x1104]);

        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            new CpuContext(memory, Generation.Gen5),
            state,
            out var evaluation,
            out var error), error);

        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(0UL, binding.DescriptorSourceAddress);
        var chain = Assert.IsType<Gen5DescriptorChain>(binding.DeferredChain);
        Assert.Equal(0x1100UL, chain.Anchor0);
        Assert.Equal(0x1104UL, chain.Anchor1);
        var step = Assert.Single(chain.Steps);
        Assert.Equal(0UL, step.Offset);
        Assert.False(step.ViaBufferDescriptor);
    }

    private static Gen5ShaderState CreateState(
        IReadOnlyList<uint> userData,
        IReadOnlyList<ulong>? userDataSources)
    {
        var scalarLoad = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx8",
            [],
            [Gen5Operand.Scalar(0)],
            Enumerable.Range(4, 8)
                .Select(static index => Gen5Operand.Scalar((uint)index))
                .ToArray(),
            new Gen5ScalarMemoryControl(8, 0, null));
        var image = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Mimg,
            "ImageLoad",
            [],
            [],
            [],
            new Gen5ImageControl(
                Dmask: 1,
                VectorAddress: 0,
                AddressRegisters: [],
                VectorData: 0,
                ScalarResource: 4,
                ScalarSampler: 12,
                Dimension: 1,
                IsArray: false,
                Glc: false,
                Slc: false,
                A16: false,
                D16: false));
        return new Gen5ShaderState(
            new Gen5ShaderProgram(0x4000, [scalarLoad, image]),
            userData,
            Metadata: null,
            UserDataSources: userDataSources);
    }
}
