// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcGraphicsGlobalBufferPackingTests
{
    [Fact]
    public void ReadOnlyBindings_ShareOneAlignedDescriptor()
    {
        Gen5GlobalMemoryBinding[] bindings =
        [
            CreateBinding(0x1000, [1, 2, 3, 4]),
            CreateBinding(0x2000, [5, 6, 7, 8]),
            CreateBinding(0x3000, [9, 10, 11, 12]),
        ];

        var plan = AgcExports.CreateGraphicsGlobalBufferPackingPlan(
            bindings,
            bakeScalars: true);

        Assert.True(plan.Packed);
        Assert.Equal(1, plan.DescriptorCount);
        Assert.Equal([0, 0, 0], plan.DescriptorIndices);
        Assert.Equal([0u, 64u, 128u], plan.DwordOffsets);
        Assert.Equal(516, plan.PackedByteLength);
    }

    [Fact]
    public void WritableBinding_DoesNotUseSnapshotPacking()
    {
        Gen5GlobalMemoryBinding[] bindings =
        [
            CreateBinding(0x1000, [1, 2, 3, 4]),
            CreateBinding(0x2000, [5, 6, 7, 8], writable: true),
            CreateBinding(0x3000, [9, 10, 11, 12]),
        ];

        var plan = AgcExports.CreateGraphicsGlobalBufferPackingPlan(
            bindings,
            bakeScalars: true);

        Assert.False(plan.Packed);
        Assert.Equal(bindings.Length, plan.DescriptorCount);
        Assert.Null(plan.DescriptorIndices);
        Assert.Null(plan.DwordOffsets);
    }

    [Fact]
    public void FewerThanTwoReadOnlyBindings_DoesNotPack()
    {
        Gen5GlobalMemoryBinding[] bindings =
        [
            CreateBinding(0x1000, [1, 2, 3, 4], writable: true),
            CreateBinding(0x2000, [5, 6, 7, 8], writable: true),
            CreateBinding(0x3000, [9, 10, 11, 12]),
        ];

        var plan = AgcExports.CreateGraphicsGlobalBufferPackingPlan(
            bindings,
            bakeScalars: true);

        Assert.False(plan.Packed);
        Assert.Equal(bindings.Length, plan.DescriptorCount);
        Assert.Null(plan.DescriptorIndices);
        Assert.Null(plan.DwordOffsets);
    }

    [Theory]
    [InlineData(12, 7, 8, true, true)]
    [InlineData(11, 7, 8, true, false)]
    [InlineData(14, 0, 14, true, false)]
    [InlineData(14, 7, 7, false, false)]
    public void StageSplit_OnlyTargetsLargeMacOsGraphicsLayouts(
        int mergedCount,
        int pixelCount,
        int vertexCount,
        bool isMacOs,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.RequiresSplitStageGraphicsGlobalBuffers(
                mergedCount,
                pixelCount,
                vertexCount,
                isMacOs));
    }

    private static Gen5GlobalMemoryBinding CreateBinding(
        ulong baseAddress,
        byte[] data,
        bool writable = false) =>
        new(
            ScalarAddress: 0,
            baseAddress,
            InstructionPcs: [],
            data,
            data.Length,
            DataPooled: false)
        {
            Writable = writable,
            WriteBackToGuest = writable,
        };
}
