// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5SpirvPackedHalfExportTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint PsUserDataRegister = 0x0C;

    [Fact]
    public void CompressedExport_UnpacksTheStoredHalfPrecisionVgpr()
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(
            memory,
            ShaderAddress,
            [
                // v_cvt_pkrtz_f16_f32 v2, v2, v3
                0x5E040702,
                // v_cvt_pkrtz_f16_f32 v3, v4, v5
                0x5E060B04,
                // exp mrt0, v2, v3 compr done vm
                0xF8001C0F, 0x00000302,
            ]);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            // SPI_SHADER_PGM_RSRC2_PS advertises zero user SGPRs.
            [PsUserDataRegister - 1] = 0,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                PsUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out error),
            error);

        // GLSL.std.450 instruction 62 is UnpackHalf2x16. The compressed export
        // must consume the packed VGPR value, including its half-precision
        // rounding, instead of bypassing it through a float32 shadow value.
        Assert.Contains(62u, CollectExtInstNumbers(shader.Spirv));
    }

    [Fact]
    public void PixelDpp_DeclaresAndUsesSubgroupShuffle()
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(
            memory,
            ShaderAddress,
            [
                // v_mov_b32_dpp v9, v7 quad_perm:[0,0,0,0]
                0x7E1202FA, 0xFF080007,
                // exp mrt0, v9, v9, v9, v9 done vm
                0xF800081F, 0x09090909,
            ]);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [PsUserDataRegister - 1] = 0,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                PsUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out error),
            error);

        var capabilities = CollectCapabilities(shader.Spirv);
        Assert.Contains((uint)SpirvCapability.GroupNonUniform, capabilities);
        Assert.Contains((uint)SpirvCapability.GroupNonUniformShuffle, capabilities);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, CollectOpcodes(shader.Spirv));
        Assert.Contains((uint)SpirvDecoration.Flat, CollectDecorations(shader.Spirv));
    }

    private static HashSet<uint> CollectExtInstNumbers(byte[] spirv)
    {
        var instructions = new HashSet<uint>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            if ((ushort)word == (ushort)SpirvOp.ExtInst && wordCount >= 5)
            {
                instructions.Add(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        spirv.AsSpan(offset + (4 * sizeof(uint)), sizeof(uint))));
            }

            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static HashSet<uint> CollectCapabilities(byte[] spirv)
    {
        var capabilities = new HashSet<uint>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            if ((ushort)word == (ushort)SpirvOp.Capability && wordCount >= 2)
            {
                capabilities.Add(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        spirv.AsSpan(offset + sizeof(uint), sizeof(uint))));
            }

            offset += wordCount * sizeof(uint);
        }

        return capabilities;
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }

    private static HashSet<uint> CollectDecorations(byte[] spirv)
    {
        var decorations = new HashSet<uint>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            if ((ushort)word == (ushort)SpirvOp.Decorate && wordCount >= 3)
            {
                decorations.Add(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        spirv.AsSpan(offset + (2 * sizeof(uint)), sizeof(uint))));
            }

            offset += wordCount * sizeof(uint);
        }

        return decorations;
    }
}
