// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// The VOP3-only opcodes (>= 0x140) and the VOP3P packed table, diffed against
/// the RDNA2 ISA listing. Silent Hill: The Short Message dies in pixel shader
/// 0x1D1624AEF648BBD5 at pc=0x9D8 on V_MIN3_F16 (VOP3 opcode 0x351).
/// </summary>
public sealed class Gen5Vop3OnlyCoverageTests
{
    private const uint SEndpgm = 0xBF810000;

    /// <summary>
    /// VOP3A: 0b110101 prefix, op[25:16], clamp[15], op_sel[14:11], abs[10:8],
    /// vdst[7:0]; then src0[8:0], src1[17:9], src2[26:18], omod[28:27], neg[31:29].
    /// </summary>
    private static (uint Low, uint High) Vop3(
        uint opcode,
        uint vdst = 0,
        uint src0 = 257,
        uint src1 = 258,
        uint src2 = 259,
        uint operandSelect = 0,
        uint absolute = 0,
        bool clamp = false,
        uint negate = 0,
        uint outputModifier = 0) =>
        ((0x35u << 26) |
            (opcode << 16) |
            (clamp ? 1u << 15 : 0u) |
            (operandSelect << 11) |
            (absolute << 8) |
            vdst,
        src0 | (src1 << 9) | (src2 << 18) | (outputModifier << 27) | (negate << 29));

    /// <summary>
    /// VOP3P: 0b110011 prefix, op[22:16], op_sel_hi[2][14], neg_hi[10:8],
    /// op_sel[13:11], clamp[15], vdst[7:0]; then sources, op_sel_hi[1:0][28:27],
    /// neg_lo[31:29].
    /// </summary>
    private static (uint Low, uint High) Vop3p(
        uint opcode,
        uint vdst = 0,
        uint src0 = 257,
        uint src1 = 258,
        uint src2 = 259) =>
        ((0x33u << 26) | (opcode << 16) | vdst,
        src0 | (src1 << 9) | (src2 << 18));

    public static TheoryData<uint, string> Vop3OnlyOpcodes() => new()
    {
        { 0x2FF, "VLshlrevB64" },
        { 0x301, "VAshrrevI64" },
        { 0x304, "VSubNcU16" },
        { 0x305, "VMulLoU16" },
        { 0x307, "VLshrrevB16" },
        { 0x308, "VAshrrevI16" },
        { 0x309, "VMaxU16" },
        { 0x30A, "VMaxI16" },
        { 0x30B, "VMinU16" },
        { 0x30C, "VMinI16" },
        { 0x30D, "VAddNcI16" },
        { 0x30E, "VSubNcI16" },
        { 0x311, "VPackB32F16" },
        { 0x312, "VCvtPknormI16F16" },
        { 0x313, "VCvtPknormU16F16" },
        { 0x314, "VLshlrevB16" },
        { 0x340, "VMadU16" },
        { 0x344, "VPermB32" },
        { 0x351, "VMin3F16" },
        { 0x352, "VMin3I16" },
        { 0x353, "VMin3U16" },
        { 0x354, "VMax3F16" },
        { 0x355, "VMax3I16" },
        { 0x356, "VMax3U16" },
        { 0x357, "VMed3F16" },
        { 0x358, "VMed3I16" },
        { 0x359, "VMed3U16" },
        { 0x35E, "VMadI16" },
        { 0x35F, "VDivFixupF16" },
        { 0x375, "VMadI32I16" },
        { 0x376, "VSubNcI32" },
        { 0x37F, "VAddNcI32" },
        { 0x140, "VFmaLegacyF32" },
        { 0x142, "VMadI32I24" },
        { 0x15F, "VDivFixupF32" },
    };

    [Theory]
    [MemberData(nameof(Vop3OnlyOpcodes))]
    public void Vop3OnlyOpcodesDecodeAndCompile(uint opcode, string expectedName)
    {
        // vdst 2 so the 64-bit shifts have a legal v2/v3 pair.
        var (low, high) = Vop3(opcode, vdst: 2);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, program.Instructions[0].Encoding);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [MemberData(nameof(Vop3OnlyOpcodes))]
    public void Vop3OnlyOpcodesAcceptModifiersAndOpsel(uint opcode, string expectedName)
    {
        // op_sel[0] and op_sel[3] plus abs/neg/clamp/omod on every operand path.
        var (low, high) = Vop3(
            opcode,
            vdst: 2,
            operandSelect: 0x9,
            absolute: 0x1,
            clamp: true,
            negate: 0x1,
            outputModifier: 1);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void Min3Float16IsTheSilentHillBlocker()
    {
        // The exact encoding the game's pixel shader dies on.
        var (low, high) = Vop3(0x351, vdst: 0, src0: 257, src1: 258, src2: 259);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal("VMin3F16", program.Instructions[0].Opcode);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        // The f16 result merges into the destination's selected half.
        Assert.Contains(0xFFFF_0000u, ReadUIntConstants(shader.Spirv));
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void MadInt64Int32DecodesThroughVop3B()
    {
        // VOP3B opcode 0x177; the carry-out SGPR lives in the sdst field.
        var (low, high) = Vop3(0x177, vdst: 2);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal("VMadI64I32", program.Instructions[0].Opcode);
    }

    [Theory]
    [InlineData(0x00u, "VPkMadI16")]
    [InlineData(0x01u, "VPkMulLoU16")]
    [InlineData(0x02u, "VPkAddI16")]
    [InlineData(0x03u, "VPkSubI16")]
    [InlineData(0x04u, "VPkLshlrevB16")]
    [InlineData(0x05u, "VPkLshrrevB16")]
    [InlineData(0x06u, "VPkAshrrevI16")]
    [InlineData(0x07u, "VPkMaxI16")]
    [InlineData(0x08u, "VPkMinI16")]
    [InlineData(0x09u, "VPkMadU16")]
    [InlineData(0x0Au, "VPkAddU16")]
    [InlineData(0x0Bu, "VPkSubU16")]
    [InlineData(0x0Cu, "VPkMaxU16")]
    [InlineData(0x0Du, "VPkMinU16")]
    [InlineData(0x13u, "VDot2F32F16")]
    [InlineData(0x14u, "VDot2I32I16")]
    [InlineData(0x15u, "VDot2U32U16")]
    [InlineData(0x16u, "VDot4I32I8")]
    [InlineData(0x17u, "VDot4U32U8")]
    [InlineData(0x18u, "VDot8I32I4")]
    [InlineData(0x19u, "VDot8U32U4")]
    public void Vop3pGapsDecodeAndCompile(uint opcode, string expectedName)
    {
        var (low, high) = Vop3p(opcode);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3p, program.Instructions[0].Encoding);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Gen5Vop1CoverageTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    // No f64 domain in either back end: these decode so the failure names the
    // instruction rather than reporting a raw opcode.
    [InlineData(0x14Cu, "VFmaF64")]
    [InlineData(0x164u, "VAddF64")]
    [InlineData(0x168u, "VLdexpF64")]
    [InlineData(0x342u, "VInterpP1llF16")]
    [InlineData(0x35Au, "VInterpP2F16")]
    public void UnsupportedVop3OpcodesDecodeButAreRejectedByName(
        uint opcode,
        string expectedName)
    {
        var (low, high) = Vop3(opcode, vdst: 2);
        var program = Gen5Vop1CoverageTests.Decode([low, high, SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error),
            "expected the unsupported opcode to be rejected");
        Assert.Contains(expectedName, error, StringComparison.Ordinal);
    }

    private static IReadOnlyList<uint> ReadUIntConstants(byte[] spirv)
    {
        var constants = new List<uint>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            if ((ushort)header == (ushort)SpirvOp.Constant && wordCount >= 4)
            {
                constants.Add(
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                        spirv.AsSpan(offset + (3 * sizeof(uint)))));
            }

            offset += wordCount * sizeof(uint);
        }

        return constants;
    }
}
