// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The Metal back end has to cover the same VOP3-only opcodes as the SPIR-V one.
/// </summary>
public sealed class MslVop3OnlyCoverageTests
{
    private static (uint Low, uint High) Vop3(uint opcode, uint vdst) =>
        ((0x35u << 26) | (opcode << 16) | vdst,
        257u | (258u << 9) | (259u << 18));

    [Fact]
    public void Vop3OnlyOpcodesTranslateToMsl()
    {
        uint[] opcodes =
        [
            0x2FF, 0x301, 0x304, 0x305, 0x307, 0x308, 0x309, 0x30A, 0x30B, 0x30C,
            0x30D, 0x30E, 0x311, 0x312, 0x313, 0x314, 0x340, 0x344, 0x351, 0x352,
            0x353, 0x354, 0x355, 0x356, 0x357, 0x358, 0x359, 0x35E, 0x35F, 0x375,
            0x376, 0x37F, 0x140, 0x142, 0x15F,
        ];

        var words = new List<uint>();
        foreach (var opcode in opcodes)
        {
            // vdst 2 so the 64-bit shifts have a legal v2/v3 pair.
            var (low, high) = Vop3(opcode, vdst: 2);
            words.Add(low);
            words.Add(high);
        }

        words.Add(0xBF810000);

        var fixture = new Gen5ComputeFixture(
            "vop3-only",
            [.. words],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        // min3/max3/med3 in both domains, the 16-bit merges, byte permute and the
        // divide fixup's quiet NaN.
        Assert.Contains("fmin(fmin(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("fmax(fmax(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFF0000u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("0x7FC00000u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("32767.0f", shader.Source, StringComparison.Ordinal);
    }
}
