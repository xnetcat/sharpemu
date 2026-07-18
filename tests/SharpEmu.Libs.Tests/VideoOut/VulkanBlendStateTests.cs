// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanBlendStateTests
{
    [Fact]
    public void IntegerMrtTarget_DisablesOnlyItsBlendState()
    {
        var enabled = GuestBlendState.Default with
        {
            Enable = true,
            ColorSrcFactor = 4,
            WriteMask = 0x7,
        };
        GuestBlendState[] blends = [enabled, enabled];

        var normalized =
            VulkanVideoPresenter.NormalizeBlendStatesForRenderTargets(
                blends,
                [true, false]);

        Assert.Equal(enabled, normalized[0]);
        Assert.Equal(enabled with { Enable = false }, normalized[1]);
    }

    [Fact]
    public void MetalUnsupportedFloatBlend_IsDisabledWithoutChangingWriteMask()
    {
        var enabled = GuestBlendState.Default with
        {
            Enable = true,
            WriteMask = 0x3,
        };

        var normalized =
            VulkanVideoPresenter.NormalizeBlendStatesForRenderTargets(
                [enabled],
                [false]);

        Assert.False(normalized[0].Enable);
        Assert.Equal(0x3u, normalized[0].WriteMask);
    }

    [Fact]
    public void MismatchedAttachmentCounts_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            VulkanVideoPresenter.NormalizeBlendStatesForRenderTargets(
                [GuestBlendState.Default],
                []));
    }
}
