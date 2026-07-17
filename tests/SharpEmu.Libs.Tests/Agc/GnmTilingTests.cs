// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GnmTilingTests
{
    [Fact]
    public void Detile256ByteStandardBcBlockUsesAddrLibColumnMajorPattern()
    {
        const int blockBytes = 16;
        var tiled = new byte[256];
        for (var sourceElement = 0; sourceElement < 16; sourceElement++)
        {
            tiled.AsSpan(sourceElement * blockBytes, blockBytes)
                .Fill((byte)(sourceElement + 1));
        }

        var linear = new byte[256];
        Assert.True(GnmTiling.TryDetile(
            tiled,
            linear,
            swizzleMode: 1,
            elementsWide: 4,
            elementsHigh: 4,
            bytesPerElement: blockBytes));

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                // AMD's 16-byte GFX10_SW_256_S pattern is
                // address bits 4..7 = y0, y1, x0, x1.
                var expectedSourceElement = y + x * 4;
                var linearElement = y * 4 + x;
                Assert.All(
                    linear.AsSpan(linearElement * blockBytes, blockBytes).ToArray(),
                    value => Assert.Equal((byte)(expectedSourceElement + 1), value));
            }
        }
    }

    [Fact]
    public void TiledByteCountRoundsBcAtlasToWhole256ByteBlocks()
    {
        Assert.True(GnmTiling.TryGetTiledByteCount(
            swizzleMode: 1,
            elementsWide: 20,
            elementsHigh: 20,
            bytesPerElement: 16,
            out var byteCount));

        Assert.Equal(6_400ul, byteCount);
    }
}
