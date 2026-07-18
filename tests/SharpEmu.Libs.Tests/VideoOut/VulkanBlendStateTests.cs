// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanBlendStateTests
{
    [Fact]
    public void NonBlendableMrtTarget_DisablesOnlyItsBlendState()
    {
        var enabled = GuestBlendState.Default with
        {
            Enable = true,
            ColorSrcFactor = 4,
            WriteMask = 0x7,
        };
        GuestBlendState[] blends = [enabled, enabled];

        var normalized =
            GuestBlendStateNormalizer.NormalizeNonBlendableAttachments(
                blends,
                [true, false],
                out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.Equal(enabled, normalized[0]);
        Assert.Equal(enabled with { Enable = false }, normalized[1]);
    }

    [Fact]
    public void NonBlendableTarget_IsDisabledWithoutChangingWriteMask()
    {
        var enabled = GuestBlendState.Default with
        {
            Enable = true,
            WriteMask = 0x3,
        };

        var normalized =
            GuestBlendStateNormalizer.NormalizeNonBlendableAttachments(
                [enabled],
                [false],
                out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.False(normalized[0].Enable);
        Assert.Equal(0x3u, normalized[0].WriteMask);
    }

    [Fact]
    public void DisabledBlendOnNonBlendableTarget_IsNotCounted()
    {
        var normalized =
            GuestBlendStateNormalizer.NormalizeNonBlendableAttachments(
                [GuestBlendState.Default],
                [false],
                out var normalizedCount);

        Assert.Equal(0, normalizedCount);
        Assert.Equal(GuestBlendState.Default, normalized[0]);
    }

    [Fact]
    public void MismatchedAttachmentCounts_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            GuestBlendStateNormalizer.NormalizeNonBlendableAttachments(
                [GuestBlendState.Default],
                [],
                out _));
    }
}
