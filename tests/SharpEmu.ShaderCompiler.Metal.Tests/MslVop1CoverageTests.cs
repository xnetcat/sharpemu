// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The Metal back end has to cover the same VOP1 encodings as the SPIR-V one;
/// a missing opcode there fails the whole program with
/// "unsupported vector opcode".
/// </summary>
public sealed class MslVop1CoverageTests
{
    /// <summary>VOP1: 0b0111111 prefix, vdst[24:17], op[16:9], src0[8:0].</summary>
    private static uint Vop1(uint opcode, uint vdst, uint src0) =>
        0x7E00_0000u | (vdst << 17) | (opcode << 9) | src0;

    [Fact]
    public void Float16UnaryFamilyTranslatesToMsl()
    {
        // v_cvt_f16_u16 / _f16_i16 / _u16_f16 / _i16_f16, rcp, sqrt, rsq, log,
        // exp, frexp mant/exp, floor, ceil, trunc, rndne, fract, sin, cos.
        var words = new List<uint>();
        for (var opcode = 0x50u; opcode <= 0x61u; opcode++)
        {
            words.Add(Vop1(opcode, opcode - 0x50u, 257));
        }

        words.Add(0xBF810000); // s_endpgm

        var fixture = new Gen5ComputeFixture(
            "vop1-f16-family",
            [.. words],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        foreach (var expected in new[]
        {
            "rcp", "sqrt(", "rsqrt(", "log2(", "exp2(", "floor(", "ceil(",
            "trunc(", "rint(", "fract(", "sin(", "cos(",
        })
        {
            if (expected == "rcp")
            {
                Assert.Contains("1.0f / ", shader.Source, StringComparison.Ordinal);
                continue;
            }

            Assert.Contains(expected, shader.Source, StringComparison.Ordinal);
        }

        // Every 16-bit result merges into the selected VGPR half.
        Assert.Contains("& 0xFFFF0000u", shader.Source, StringComparison.Ordinal);
        // frexp is exponent arithmetic on the widened f32 bit pattern.
        Assert.Contains("0x7F800000u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("0x807FFFFFu", shader.Source, StringComparison.Ordinal);
    }
}
