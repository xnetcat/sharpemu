// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanVertexBindingPlanTests
{
    [Fact]
    public void InterleavedAttributes_ShareOneBinding()
    {
        VulkanVertexBindingSource[] sources =
        [
            new(BufferIdentity: 1, Stride: 16, Offset: 12),
            new(BufferIdentity: 1, Stride: 16, Offset: 0),
            new(BufferIdentity: 1, Stride: 16, Offset: 8),
        ];

        var plan = VulkanVideoPresenter.PlanVertexBindings(sources);

        Assert.Equal([0], plan.BindingSourceIndices);
        Assert.Equal([16u], plan.BindingStrides);
        Assert.Equal([0UL], plan.BindingOffsets);
        Assert.Equal([0u, 0u, 0u], plan.AttributeBindings);
        Assert.Equal([12u, 0u, 8u], plan.AttributeOffsets);
    }

    [Fact]
    public void DifferentRecords_KeepSeparateBindings()
    {
        VulkanVertexBindingSource[] sources =
        [
            new(BufferIdentity: 1, Stride: 16, Offset: 0),
            new(BufferIdentity: 1, Stride: 16, Offset: 16),
        ];

        var plan = VulkanVideoPresenter.PlanVertexBindings(sources);

        Assert.Equal([0, 1], plan.BindingSourceIndices);
        Assert.Equal([0UL, 16UL], plan.BindingOffsets);
        Assert.Equal([0u, 1u], plan.AttributeBindings);
        Assert.Equal([0u, 0u], plan.AttributeOffsets);
    }

    [Theory]
    [InlineData(1UL, 16u, 2UL, 16u)]
    [InlineData(1UL, 16u, 1UL, 32u)]
    public void DifferentStreams_KeepSeparateBindings(
        ulong firstBuffer,
        uint firstStride,
        ulong secondBuffer,
        uint secondStride)
    {
        VulkanVertexBindingSource[] sources =
        [
            new(firstBuffer, firstStride, Offset: 0),
            new(secondBuffer, secondStride, Offset: 0),
        ];

        var plan = VulkanVideoPresenter.PlanVertexBindings(sources);

        Assert.Equal([0, 1], plan.BindingSourceIndices);
        Assert.Equal([0u, 1u], plan.AttributeBindings);
    }

    [Fact]
    public void StaleDescriptorStride_IsExpandedToFitAttribute()
    {
        VulkanVertexBindingSource[] sources =
        [
            new(
                BufferIdentity: 1,
                Stride: 2,
                Offset: 0,
                MinimumAttributeBytes: 12),
        ];

        var plan = VulkanVideoPresenter.PlanVertexBindings(sources);

        Assert.Equal([12u], plan.BindingStrides);
        Assert.Equal([0u], plan.AttributeOffsets);
    }

    [Theory]
    [InlineData(1u, 4u, 1u)]
    [InlineData(5u, 2u, 4u)]
    [InlineData(10u, 4u, 4u)]
    [InlineData(13u, 3u, 12u)]
    [InlineData(14u, 4u, 16u)]
    [InlineData(0xFFFFu, 3u, 12u)]
    public void VertexFormatSize_CoversMappedAndFallbackFormats(
        uint dataFormat,
        uint componentCount,
        uint expectedBytes)
    {
        Assert.Equal(
            expectedBytes,
            VulkanVideoPresenter.GetVertexFormatByteSize(
                dataFormat,
                componentCount));
    }
}
