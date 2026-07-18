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
}
