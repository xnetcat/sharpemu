// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// Encodings found by diffing the decoder's opcode tables against the RDNA2 ISA
/// listing (section 13). Each of these used to be rejected at decode.
/// </summary>
public sealed class Gen5IsaTableGapTests
{
    private const uint SEndpgm = 0xBF810000;

    private static uint Vop1(uint opcode, uint vdst, uint src0) =>
        0x7E00_0000u | (vdst << 17) | (opcode << 9) | src0;

    // VOP2/VOPC src1 is an 8-bit VGPR index, so it takes a plain register number
    // while src0 is the 9-bit source field (256 + n for a VGPR).
    /// <summary>VOP2: 0b0 prefix, op[30:25], vdst[24:17], src1[16:9], src0[8:0].</summary>
    private static uint Vop2(uint opcode, uint vdst, uint src0, uint vgpr1) =>
        (opcode << 25) | (vdst << 17) | (vgpr1 << 9) | src0;

    /// <summary>VOPC: 0b0111110 prefix, op[24:17], src1[16:9], src0[8:0].</summary>
    private static uint Vopc(uint opcode, uint src0, uint vgpr1) =>
        0x7C00_0000u | (opcode << 17) | (vgpr1 << 9) | src0;

    /// <summary>SOP1: 0b101111101 prefix, sdst[22:16], op[15:8], ssrc0[7:0].</summary>
    private static uint Sop1(uint opcode, uint sdst, uint ssrc0) =>
        0xBE80_0000u | (sdst << 16) | (opcode << 8) | ssrc0;

    /// <summary>SOP2: 0b10 prefix, op[29:23], sdst[22:16], ssrc1[15:8], ssrc0[7:0].</summary>
    private static uint Sop2(uint opcode, uint sdst, uint ssrc0, uint ssrc1) =>
        0x8000_0000u | (opcode << 23) | (sdst << 16) | (ssrc1 << 8) | ssrc0;

    [Theory]
    [InlineData(0x06u, "VFmacLegacyF32")]
    [InlineData(0x07u, "VMulLegacyF32")]
    [InlineData(0x0Du, "VDot4cI32I8")]
    [InlineData(0x3Cu, "VPkFmacF16")]
    [InlineData(0x0Au, "VMulHiI32I24")]
    [InlineData(0x36u, "VFmacF16")]
    [InlineData(0x3Bu, "VLdexpF16")]
    public void Vop2GapsDecodeAndCompile(uint opcode, string expectedName)
    {
        var program = Gen5Vop1CoverageTests.Decode(
            [Vop2(opcode, 0, 257, 2), SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop2, program.Instructions[0].Encoding);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0x8Fu, "VCmpClassF16")]
    [InlineData(0x9Fu, "VCmpxClassF16")]
    [InlineData(0xA9u, "VCmpLtU16")]
    [InlineData(0xAAu, "VCmpEqU16")]
    [InlineData(0xABu, "VCmpLeU16")]
    [InlineData(0xACu, "VCmpGtU16")]
    [InlineData(0xADu, "VCmpNeU16")]
    [InlineData(0xAEu, "VCmpGeU16")]
    [InlineData(0xB9u, "VCmpxLtU16")]
    [InlineData(0xBEu, "VCmpxGeU16")]
    public void VopcGapsDecodeAndCompile(uint opcode, string expectedName)
    {
        var program = Gen5Vop1CoverageTests.Decode([Vopc(opcode, 257, 2), SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0x05u, "SCmovB32")]
    [InlineData(0x06u, "SCmovB64")]
    [InlineData(0x0Du, "SBcnt0I32B32")]
    [InlineData(0x11u, "SFF0I32B32")]
    [InlineData(0x15u, "SFlbitI32B32")]
    [InlineData(0x17u, "SFlbitI32")]
    [InlineData(0x19u, "SSextI32I8")]
    [InlineData(0x1Au, "SSextI32I16")]
    public void Sop1GapsDecodeAndCompile(uint opcode, string expectedName)
    {
        // s2/s3 so the B64 form has a legal aligned pair.
        var program = Gen5Vop1CoverageTests.Decode([Sop1(opcode, 2, 4), SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sop1, program.Instructions[0].Encoding);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void Float16FmaLiteralFormsDecodeWithTheirLiteralOperand()
    {
        // V_FMAMK_F16 (0x37): D.f16 = S0.f16 * K.f16 + S1.f16, so the decoder puts
        // the literal between the two register sources.
        var mk = Gen5Vop1CoverageTests.Decode(
            [(0x37u << 25) | (2u << 17) | (1u << 9) | 256u, 0xDEAD4400u, SEndpgm]);
        Assert.Equal("VFmaMkF16", mk.Instructions[0].Opcode);
        Assert.Equal(3, mk.Instructions[0].Sources.Count);
        Assert.Equal(Gen5OperandKind.LiteralConstant, mk.Instructions[0].Sources[1].Kind);
        Assert.Equal(0xDEAD4400u, mk.Instructions[0].Sources[1].Value);

        // V_FMAAK_F16 (0x38): D.f16 = S0.f16 * S1.f16 + K.f16, literal last.
        var ak = Gen5Vop1CoverageTests.Decode(
            [(0x38u << 25) | (3u << 17) | (1u << 9) | 256u, 0xBEEF4400u, SEndpgm]);
        Assert.Equal("VFmaAkF16", ak.Instructions[0].Opcode);
        Assert.Equal(3, ak.Instructions[0].Sources.Count);
        Assert.Equal(Gen5OperandKind.LiteralConstant, ak.Instructions[0].Sources[2].Kind);
        Assert.Equal(0xBEEF4400u, ak.Instructions[0].Sources[2].Value);
    }

    [Fact]
    public void SilentHillFmaakFloat16WordDecodesAndCompiles()
    {
        // The exact word Silent Hill dies on: pixel shader 0xE3CF062DC905BE5D,
        // "unknown-vop2 op=0x38 word=0x700608F5". src0 is the inline constant
        // -2.0 (245), src1 is v4 and the destination is v3.
        var program = Gen5Vop1CoverageTests.Decode([0x700608F5u, 0x00003C00u, SEndpgm]);
        Assert.Equal("VFmaAkF16", program.Instructions[0].Opcode);
        Assert.Equal(Gen5OperandKind.EncodedConstant, program.Instructions[0].Sources[0].Kind);
        Assert.Equal(245u, program.Instructions[0].Sources[0].Value);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void ScalarAbsoluteDifferenceUsesTheRdna2Opcode()
    {
        // S_ABSDIFF_I32 is SOP2 opcode 0x2C in the RDNA2 listing; the decoder had
        // it at 0x2D, so a real s_absdiff_i32 was rejected as unknown-sop2.
        var program = Gen5Vop1CoverageTests.Decode([Sop2(0x2C, 2, 4, 5), SEndpgm]);
        Assert.Equal("SAbsdiffI32", program.Instructions[0].Opcode);

        Assert.False(
            Gen5Vop1CoverageTests.TryDecode(
                [Sop2(0x2D, 2, 4, 5), SEndpgm],
                out _,
                out var error));
        Assert.Contains("unknown-sop2 op=0x2D", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeflushAndClrexcpAreNoOps()
    {
        var program = Gen5Vop1CoverageTests.Decode(
            [Vop1(0x1B, 0, 257), Vop1(0x41, 0, 257), SEndpgm]);
        Assert.Equal(
            ["VPipeflush", "VClrexcp", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error),
            error);
    }
}
