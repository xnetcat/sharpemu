// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// End-to-end pipeline tests: synthetic GFX10 program -> decode -> scalar evaluation -> SPIR-V.
// Each test asserts the expected OpAtomic* instructions land in the emitted module.
public sealed class Gen5SpirvAtomicTranslationTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x1_0000_1000;

    [Fact]
    public void BufferAtomics_EmitAtomicOpcodes()
    {
        // BUFFER_ATOMIC_UMAX v1, BUFFER_ATOMIC_CMPSWAP v[1:2], BUFFER_ATOMIC_INC v1,
        // all against the V# in s[0:3].
        var opcodes = CompileCompute(
            [
                0xE0E04008, 0x80000100,
                0xE0C44000, 0x80000100,
                0xE0F00000, 0x80000100,
            ],
            BufferDescriptorRegisters());

        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicIIncrement, opcodes);
    }

    [Fact]
    public void DataShareAtomics_EmitAtomicOpcodes()
    {
        // DS_ADD_RTN_U32 v3, v0, v1; DS_CMPST_RTN_B32 v3, v0, v1, v2; DS_MAX_U32 v0, v1.
        var opcodes = CompileCompute(
            [
                0xD8800000, 0x03000100,
                0xD8C00000, 0x03020100,
                0xD8200000, 0x00000100,
            ],
            new Dictionary<uint, uint>());

        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
    }

    [Fact]
    public void ImageAtomicAdd_EmitsTexelPointerAndAtomicAdd()
    {
        // IMAGE_ATOMIC_ADD v2, v[0:1], s[4:11] dmask:0x1 dim:2D glc against an R32ui T#.
        var opcodes = CompileCompute(
            [0xF0442100, 0x00010200],
            new Dictionary<uint, uint>
            {
                // Descriptor word1 dataFormat (bits 28:20) = 20 selects R32ui/Uint.
                [5] = 20u << 20,
            });

        Assert.Contains((ushort)SpirvOp.ImageTexelPointer, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
    }

    [Fact]
    public void Rdna2SceneShaderOpcodes_TranslateToSpirv()
    {
        var opcodes = CompileCompute(
            [
                0xBE8A1000,             // S_BCNT1_I32_B64 s10, s[0:1]
                0x7E06A4F9, 0x00061500, // V_CVT_U16_F16 v3, v0 (SDWA)
                0x7E0CAEF9, 0x00251501, // V_LOG_F16 v6, v1 (SDWA)
                0x7E12B0F9, 0x0005150A, // V_EXP_F16 v9, v10 (SDWA)
                0xD5490003, 0x040A0300, // V_BFE_I32 v3, v0, v1, v2
                0xD76A0003, 0x040A0300, // V_CVT_PK_U16_U32 v3, v0, v1
                0xD1780003, 0x040A0300, // V_XOR3_B32 v3, v0, v1, v2
                0xD2FF0003, 0x00020300, // V_LSHLREV_B64 v[3:4], v0, v[1:2]
                0xD3000003, 0x00020300, // V_LSHRREV_B64 v[3:4], v0, v[1:2]
                0xDAC00102, 0x00001700, // DS_WRITE_ADDTID_B32 v23 offset:0x102
                0xDAC40100, 0x39000000, // DS_READ_ADDTID_B32 v57 offset:0x100
            ],
            new Dictionary<uint, uint>
            {
                [0] = 0xFFFF_FFFF,
                [1] = 0x8000_0001,
            });

        Assert.Contains((ushort)SpirvOp.BitCount, opcodes);
        Assert.Contains((ushort)SpirvOp.ConvertFToU, opcodes);
        Assert.Contains((ushort)SpirvOp.BitFieldSExtract, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftLeftLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, opcodes);
    }

    private static Dictionary<uint, uint> BufferDescriptorRegisters() => new()
    {
        // V# in s[0:3]: base=BufferAddress, stride=0, numRecords=64 bytes, type=0.
        [0] = unchecked((uint)BufferAddress),
        [1] = (uint)(BufferAddress >> 32),
        [2] = 64,
        [3] = 0,
    };

    private static HashSet<ushort> CompileCompute(
        uint[] programWords,
        Dictionary<uint, uint> userDataSgprs)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        // COMPUTE_PGM_RSRC2 advertises 16 user SGPRs; the user data words at
        // COMPUTE_USER_DATA_0 + index seed s[0..15] for the scalar evaluator.
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };
        foreach (var (sgpr, value) in userDataSgprs)
        {
            shaderRegisters[Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + sgpr] = value;
        }

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return CollectOpcodes(shader.Spirv);
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }
}
