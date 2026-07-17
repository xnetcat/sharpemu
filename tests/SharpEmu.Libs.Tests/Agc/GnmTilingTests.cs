// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GnmTilingTests
{
    [Fact]
    public void Detile256ByteStandardBcBlockUsesAddrLibPattern()
    {
        const int bytesPerBlock = 16;
        var tiled = new byte[256];
        for (var sourceBlock = 0; sourceBlock < 16; sourceBlock++)
        {
            tiled.AsSpan(sourceBlock * bytesPerBlock, bytesPerBlock)
                .Fill((byte)(sourceBlock + 1));
        }

        var linear = new byte[256];
        Assert.True(GnmTiling.TryDetile(
            tiled,
            linear,
            swizzleMode: 1,
            elementsWide: 4,
            elementsHigh: 4,
            bytesPerElement: bytesPerBlock));

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                // GFX10_SW_PATTERN_NIBBLE01[4]:
                // byte address bits 4..7 = y0, y1, x0, x1.
                var expectedSourceBlock = y + x * 4;
                var linearBlock = y * 4 + x;
                Assert.All(
                    linear.AsSpan(linearBlock * bytesPerBlock, bytesPerBlock).ToArray(),
                    value => Assert.Equal((byte)(expectedSourceBlock + 1), value));
            }
        }
    }

    [Fact]
    public void TiledByteCountRoundsBcAtlasToWhole256ByteBlocks()
    {
        Assert.True(GnmTiling.TryGetTiledByteCount(
            swizzleMode: 1,
            elementsWide: 23,
            elementsHigh: 23,
            bytesPerElement: 16,
            out var byteCount));

        Assert.Equal(9_216ul, byteCount);
    }
}
