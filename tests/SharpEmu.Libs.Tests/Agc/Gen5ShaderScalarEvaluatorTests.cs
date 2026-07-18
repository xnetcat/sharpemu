// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderScalarEvaluatorTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void NullBaseBufferDescriptor_IsTreatedAsUnbound()
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(
            memory,
            ShaderAddress,
            [
                // S_BUFFER_LOAD_DWORDX4 s[16:19], s[12:15], 0. This is the
                // exact instruction emitted by Silent Hill's affected shader.
                0xF4280406,
                0xFA000000,
            ]);

        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 12] = 0,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 13] = 0x0070_0000,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 14] = 0x006B_0000,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 15] = 0x0080_4DF0,
        };
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
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out error),
            error);
        Assert.Equal(0u, evaluation.ScalarRegisters[16]);
        Assert.Equal(0u, evaluation.ScalarRegisters[17]);
        Assert.Equal(0u, evaluation.ScalarRegisters[18]);
        Assert.Equal(0u, evaluation.ScalarRegisters[19]);
    }

    [Fact]
    public void NonCanonicalBufferDescriptor_BindsBoundedZeroBuffer()
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(
            memory,
            ShaderAddress,
            [
                // BUFFER_ATOMIC_UMAX v1, off, s[0:3], 128 offset:8 glc.
                0xE0E04008,
                0x80000100,
            ]);

        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 4u << 1,
            // Exact stale V# observed after Silent Hill's title movie. It
            // decodes as address 0x0000F00000000092 and a 19.9-GiB range.
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister] = 0x0000_0092,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 1] = 0x00FF_F000,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 2] = 0x0500_0000,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 3] = 0,
        };
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
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out error),
            error);

        // The impossible address must not become a multi-GiB zero allocation;
        // the instruction binds the same bounded zero buffer a null V# gets.
        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(0UL, binding.BaseAddress);
        Assert.Equal(sizeof(uint), binding.DataLength);
        Assert.True(binding.Writable);
    }
}
