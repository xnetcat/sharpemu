// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Decodes synthetic GFX10 atomic instructions and checks the opcode names and operand wiring
// that the SPIR-V translator relies on. Word layouts follow the RDNA2 ISA manual:
// MUBUF op=word0[24:18], MIMG op=word0[24:18], DS op=word0[25:18].
public sealed class Gen5ShaderAtomicDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint EndPgm = 0xBF810000;

    // Compute-stage register block: COMPUTE_USER_DATA_0 and COMPUTE_PGM_RSRC2,
    // required since TryCreateState validates the USER_SGPR count.
    internal const uint ComputeUserDataRegister = 0x240;
    internal const uint ComputePgmRsrc2Register = 0x213;

    [Fact]
    public void BufferAtomicUmax_DecodesControlAndDestination()
    {
        // BUFFER_ATOMIC_UMAX v1, off, s[0:3], 128 offset:8 glc
        var instruction = DecodeSingle(0xE0E04008, 0x80000100);

        Assert.Equal("BufferAtomicUmax", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.Equal(0u, control.ScalarResource);
        Assert.Equal(8, control.OffsetBytes);
        Assert.True(control.Glc);
        Assert.Equal(new[] { Gen5Operand.Vector(1) }, instruction.Destinations);
    }

    [Fact]
    public void BufferAtomicCmpswap_UsesTwoDataRegisters()
    {
        // BUFFER_ATOMIC_CMPSWAP v[1:2], off, s[0:3], 128 glc
        var instruction = DecodeSingle(0xE0C44000, 0x80000100);

        Assert.Equal("BufferAtomicCmpswap", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
    }

    [Fact]
    public void ImageAtomicAdd_KeepsDataRegisterAsDestination()
    {
        // IMAGE_ATOMIC_ADD v2, v[0:1], s[4:11] dmask:0x1 dim:2D glc
        var instruction = DecodeSingle(0xF0442100, 0x00010200);

        Assert.Equal("ImageAtomicAdd", instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(2u, control.VectorData);
        Assert.Equal(4u, control.ScalarResource);
        Assert.True(control.Glc);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, instruction.Destinations);
    }

    [Fact]
    public void DsAddU32_HasAddressAndDataSourcesButNoDestination()
    {
        // DS_ADD_U32 v0, v1
        var instruction = DecodeSingle(0xD8000000, 0x00000100);

        Assert.Equal("DsAddU32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void DsAddRtnU32_WritesReturnRegister()
    {
        // DS_ADD_RTN_U32 v3, v0, v1
        var instruction = DecodeSingle(0xD8800000, 0x03000100);

        Assert.Equal("DsAddRtnU32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(3) }, instruction.Destinations);
    }

    [Fact]
    public void DsCmpstRtnB32_OrdersComparatorBeforeNewValue()
    {
        // DS_CMPST_RTN_B32 v3, v0, v1, v2 - DATA0 (v1) is the comparator, DATA1 (v2) the
        // new value, reversed relative to buffer/image cmpswap.
        var instruction = DecodeSingle(0xD8C00000, 0x03020100);

        Assert.Equal("DsCmpstRtnB32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2) },
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(3) }, instruction.Destinations);
    }

    [Fact]
    public void Rdna2SceneShaderOpcodes_DecodeWithArchitecturalOperands()
    {
        // S_BCNT1_I32_B64 s10, s[0:1].
        var scalarBitCount = DecodeSingle(0xBE8A1000);
        Assert.Equal("SBcnt1I32B64", scalarBitCount.Opcode);
        Assert.Equal(new[] { Gen5Operand.Scalar(0) }, scalarBitCount.Sources);
        Assert.Equal(new[] { Gen5Operand.Scalar(10) }, scalarBitCount.Destinations);

        // V_CVT_U16_F16 v3, v0 with an SDWA source-selection word.
        var halfConversion = DecodeSingle(0x7E06A4F9, 0x00061500);
        Assert.Equal("VCvtU16F16", halfConversion.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(0) }, halfConversion.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(3) }, halfConversion.Destinations);
        Assert.IsType<Gen5SdwaControl>(halfConversion.Control);

        // V_LOG_F16 v6, v1 (SDWA).
        var halfLogarithm = DecodeSingle(0x7E0CAEF9, 0x00251501);
        Assert.Equal("VLogF16", halfLogarithm.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(1) }, halfLogarithm.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(6) }, halfLogarithm.Destinations);
        Assert.IsType<Gen5SdwaControl>(halfLogarithm.Control);

        // V_EXP_F16 v9, v10 (SDWA).
        var halfExponential = DecodeSingle(0x7E12B0F9, 0x0005150A);
        Assert.Equal("VExpF16", halfExponential.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(10) }, halfExponential.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(9) }, halfExponential.Destinations);
        Assert.IsType<Gen5SdwaControl>(halfExponential.Control);

        // V_BFE_I32 v3, v0, v1, v2 and V_CVT_PK_U16_U32 v3, v0, v1.
        var signedBitfield = DecodeSingle(0xD5490003, 0x040A0300);
        Assert.Equal("VBfeI32", signedBitfield.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2) },
            signedBitfield.Sources);

        var packedUnsigned = DecodeSingle(0xD76A0003, 0x040A0300);
        Assert.Equal("VCvtPkU16U32", packedUnsigned.Opcode);

        var xorThree = DecodeSingle(0xD1780003, 0x040A0300);
        Assert.Equal("VXor3B32", xorThree.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2) },
            xorThree.Sources);

        // V_LSHLREV_B64 v[3:4], v0, v[1:2].
        var shiftLeft64 = DecodeSingle(0xD2FF0003, 0x00020300);
        Assert.Equal("VLshlrevB64", shiftLeft64.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            shiftLeft64.Sources.Take(2));

        // V_LSHRREV_B64 v[3:4], v0, v[1:2].
        var shiftRight64 = DecodeSingle(0xD3000003, 0x00020300);
        Assert.Equal("VLshrrevB64", shiftRight64.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            shiftRight64.Sources.Take(2));

        // DS_WRITE_ADDTID_B32 v23 offset:0x102 has no ADDR operand. The
        // effective address is M0[15:0] + offset + lane_id * 4.
        var addThreadIdWrite = DecodeSingle(0xDAC00102, 0x00001700);
        Assert.Equal("DsWriteAddtidB32", addThreadIdWrite.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(23) }, addThreadIdWrite.Sources);
        Assert.Empty(addThreadIdWrite.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(addThreadIdWrite.Control);
        Assert.Equal(2u, control.Offset0);
        Assert.Equal(1u, control.Offset1);

        // DS_READ_ADDTID_B32 v57 offset:0x100 also has no ADDR operand.
        var addThreadIdRead = DecodeSingle(0xDAC40100, 0x39000000);
        Assert.Equal("DsReadAddtidB32", addThreadIdRead.Opcode);
        Assert.Empty(addThreadIdRead.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(57) }, addThreadIdRead.Destinations);
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [ComputePgmRsrc2Register] = 0 },
                ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        return state.Program.Instructions[0];
    }

    internal static void WriteProgram(FakeCpuMemory memory, ulong address, uint[] words)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var word in words.Append(EndPgm))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, word);
            Assert.True(memory.TryWrite(address, buffer));
            address += sizeof(uint);
        }
    }
}
