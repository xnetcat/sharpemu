// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanStorageFormatTests
{
    [Theory]
    [InlineData(Format.BC1RgbaUnormBlock)]
    [InlineData(Format.BC7SrgbBlock)]
    public void CompressedStorageDescriptorsUseWritableRgbaFallback(
        Format descriptorFormat)
    {
        Assert.Equal(
            Format.R8G8B8A8Unorm,
            VulkanVideoPresenter.GetStorageCompatibleFormat(descriptorFormat));
    }

    [Fact]
    public void WritableStorageFormatIsPreserved()
    {
        Assert.Equal(
            Format.R16G16B16A16Sfloat,
            VulkanVideoPresenter.GetStorageCompatibleFormat(
                Format.R16G16B16A16Sfloat));
    }

    [Theory]
    [InlineData("ImageLoad")]
    [InlineData("ImageLoadMip")]
    public void CompressedImageLoadsUseSampledDescriptors(string opcode)
    {
        var descriptor = new uint[8];
        descriptor[1] = 169u << 20;

        Assert.False(
            Gen5ShaderTranslator.UsesStorageImageDescriptor(opcode, descriptor));
    }

    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageAtomicAdd")]
    public void CompressedImageWritesRemainStorageDescriptors(string opcode)
    {
        var descriptor = new uint[8];
        descriptor[1] = 169u << 20;

        Assert.True(
            Gen5ShaderTranslator.UsesStorageImageDescriptor(opcode, descriptor));
    }

    [Fact]
    public void UncompressedImageLoadRemainsAStorageDescriptor()
    {
        var descriptor = new uint[8];
        descriptor[1] = 56u << 20;

        Assert.True(
            Gen5ShaderTranslator.UsesStorageImageDescriptor(
                "ImageLoad",
                descriptor));
    }
}
