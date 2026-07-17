// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanSnapshotRefreshPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void SnapshotRefreshPolicy(
        bool mappedAllZero,
        bool refreshAll,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldRefreshSnapshotBuffer(
                mappedAllZero,
                refreshAll));
    }
}
