// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Converts the RDNA2 unified FORMAT field used by GFX10 buffer and image
/// descriptors into the data-format/number-format pair used by the rest of
/// SharpEmu's AGC pipeline.
/// </summary>
internal static class Gfx10UnifiedFormat
{
    public static bool TryDecode(
        uint unifiedFormat,
        out uint dataFormat,
        out uint numberFormat)
    {
        (dataFormat, numberFormat) = unifiedFormat switch
        {
            0 => (0u, 0u),
            >= 1 and <= 6 => (1u, unifiedFormat - 1),
            >= 7 and <= 13 => (2u, DecodeNumber(unifiedFormat - 7, 7)),
            >= 14 and <= 19 => (3u, unifiedFormat - 14),
            >= 20 and <= 22 => (4u, DecodeIntegerOrFloat(unifiedFormat - 20)),
            >= 23 and <= 29 => (5u, DecodeNumber(unifiedFormat - 23, 7)),
            >= 30 and <= 36 => (6u, DecodeNumber(unifiedFormat - 30, 7)),
            >= 37 and <= 43 => (7u, DecodeNumber(unifiedFormat - 37, 7)),
            >= 44 and <= 49 => (8u, unifiedFormat - 44),
            >= 50 and <= 55 => (9u, unifiedFormat - 50),
            >= 56 and <= 61 => (10u, unifiedFormat - 56),
            >= 62 and <= 64 => (11u, DecodeIntegerOrFloat(unifiedFormat - 62)),
            >= 65 and <= 71 => (12u, DecodeNumber(unifiedFormat - 65, 7)),
            >= 72 and <= 74 => (13u, DecodeIntegerOrFloat(unifiedFormat - 72)),
            >= 75 and <= 77 => (14u, DecodeIntegerOrFloat(unifiedFormat - 75)),
            128 => (1u, 9u),
            129 => (3u, 9u),
            130 => (10u, 9u),
            132 => (34u, 7u),
            133 => (16u, 0u),
            134 => (17u, 0u),
            135 => (18u, 0u),
            136 => (19u, 0u),
            140 => (4u, 7u),
            // BC formats are already unique combined format identifiers.
            // Preserve them so the Vulkan boundary can choose the matching
            // compressed format without inventing a legacy data-format code.
            >= 169 and <= 182 => (unifiedFormat, 0u),
            _ => (0u, 0u),
        };

        return unifiedFormat == 0 || dataFormat != 0;
    }

    private static uint DecodeNumber(uint offset, uint formatCount) =>
        offset == formatCount - 1 ? 7u : offset;

    private static uint DecodeIntegerOrFloat(uint offset) =>
        offset switch
        {
            0 => 4,
            1 => 5,
            _ => 7,
        };
}
