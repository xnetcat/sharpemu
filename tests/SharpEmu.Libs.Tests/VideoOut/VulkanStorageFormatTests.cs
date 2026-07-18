// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
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
}
