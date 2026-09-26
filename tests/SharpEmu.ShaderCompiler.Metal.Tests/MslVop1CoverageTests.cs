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
        Assert.Contains("0x3F000000u", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemainingVop1GapsTranslateToMsl()
    {
        var fixture = new Gen5ComputeFixture(
            "vop1-remaining",
            [
                Vop1(0x1B, 0, 257), // v_pipeflush
                Vop1(0x3B, 1, 257), // v_ffbh_i32 v1, v1
                Vop1(0x39, 2, 257), // v_ffbh_u32 v2, v1
                Vop1(0x3F, 3, 257), // v_frexp_exp_i32_f32 v3, v1
                Vop1(0x40, 4, 257), // v_frexp_mant_f32 v4, v1
                Vop1(0x41, 0, 257), // v_clrexcp
                Vop1(0x62, 5, 257), // v_sat_pk_u8_i16 v5, v1
                Vop1(0x63, 6, 257), // v_cvt_norm_i16_f16 v6, v1
                Vop1(0x64, 7, 257), // v_cvt_norm_u16_f16 v7, v1
                Vop1(0x65, 8, 257), // v_swap_b32 v8, v1
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("clz(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("32767.0f", shader.Source, StringComparison.Ordinal);
        Assert.Contains("65535.0f", shader.Source, StringComparison.Ordinal);
        Assert.Contains("0, 255)", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IsaTableGapsTranslateToMsl()
    {
        var fixture = new Gen5ComputeFixture(
            "isa-table-gaps",
            [
                (0x07u << 25) | (1u << 17) | (2u << 9) | 257u, // v_mul_legacy_f32
                (0x0Au << 25) | (2u << 17) | (2u << 9) | 257u, // v_mul_hi_i32_i24
                (0x36u << 25) | (3u << 17) | (2u << 9) | 257u, // v_fmac_f16
                (0x3Bu << 25) | (4u << 17) | (2u << 9) | 257u, // v_ldexp_f16
                0x7C00_0000u | (0xA9u << 17) | (2u << 9) | 257u, // v_cmp_lt_u16
                0x7C00_0000u | (0x8Fu << 17) | (2u << 9) | 257u, // v_cmp_class_f16
                0xBE80_0000u | (2u << 16) | (0x19u << 8) | 4u, // s_sext_i32_i8 s2, s4
                0xBE80_0000u | (3u << 16) | (0x15u << 8) | 4u, // s_flbit_i32_b32 s3, s4
                0xBE80_0000u | (5u << 16) | (0x0Du << 8) | 4u, // s_bcnt0_i32_b32 s5, s4
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("ldexp(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("popcount(~", shader.Source, StringComparison.Ordinal);
        Assert.Contains("6.103515625e-05f", shader.Source, StringComparison.Ordinal);
        Assert.Contains("0xFFFFu)) <", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    // No f64 domain in either back end: these decode so the failure names the
    // instruction, then get rejected at emission.
    [InlineData(0x17u, "VTruncF64")]
    [InlineData(0x1Au, "VFloorF64")]
    [InlineData(0x3Du, "VFrexpMantF64")]
    [InlineData(0x68u, "VSwaprelB32")]
    public void UnsupportedVop1OpcodesAreRejectedByName(uint opcode, string expectedName)
    {
        var fixture = new Gen5ComputeFixture(
            $"vop1-unsupported-{opcode:X2}",
            [Vop1(opcode, 0, 257), 0xBF810000],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var request = Gen5ComputeFixtures.CreateComputeRequest(fixture);
        Assert.False(
            Gen5MslTranslator.TryCompileProgram(request, out _, out var error),
            "expected the unsupported opcode to be rejected");
        Assert.Contains(expectedName, error, StringComparison.Ordinal);
    }
}
