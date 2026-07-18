// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

internal static class GuestBlendStateNormalizer
{
    /// <summary>
    /// Disables blending only for attachments whose backend format cannot
    /// blend (integer formats, and formats whose device reports no
    /// color-attachment-blend capability). The draw and every other MRT
    /// attachment remain valid; blending on such a target is meaningless on
    /// real hardware anyway.
    /// </summary>
    public static GuestBlendState[] NormalizeNonBlendableAttachments(
        IReadOnlyList<GuestBlendState> blends,
        IReadOnlyList<bool> supportsBlend,
        out int normalizedCount)
    {
        if (blends.Count != supportsBlend.Count)
        {
            throw new ArgumentException(
                "color attachment blend capabilities and blend states must have matching counts",
                nameof(supportsBlend));
        }

        var normalized = new GuestBlendState[blends.Count];
        normalizedCount = 0;
        for (var index = 0; index < blends.Count; index++)
        {
            var blend = blends[index];
            if (!supportsBlend[index] && blend.Enable)
            {
                blend = blend with { Enable = false };
                normalizedCount++;
            }

            normalized[index] = blend;
        }

        return normalized;
    }
}
