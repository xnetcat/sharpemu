// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcTextureCompatibilityTests
{
    [Fact]
    public void SingleTexelSampledResource_Uses2DCompatibilityPath()
    {
        Assert.True(AgcExports.ShouldBindSingleTexelAs2D(
            type: 10,
            isStorage: false,
            address: 0x1_0000,
            width: 1,
            height: 1));
    }

    [Theory]
    [InlineData(8u, false, 0x1_0000UL, 1u, 1u)]
    [InlineData(9u, false, 0x1_0000UL, 1u, 1u)]
    [InlineData(10u, true, 0x1_0000UL, 1u, 1u)]
    [InlineData(10u, false, 0UL, 1u, 1u)]
    [InlineData(10u, false, 0x1_0000UL, 2u, 1u)]
    [InlineData(10u, false, 0x1_0000UL, 1u, 2u)]
    public void OtherResources_DoNotUse2DCompatibilityPath(
        uint type,
        bool isStorage,
        ulong address,
        uint width,
        uint height)
    {
        Assert.False(AgcExports.ShouldBindSingleTexelAs2D(
            type,
            isStorage,
            address,
            width,
            height));
    }
}
