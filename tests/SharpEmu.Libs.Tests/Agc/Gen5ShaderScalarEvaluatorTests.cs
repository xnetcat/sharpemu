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
    public void NonBufferDescriptor_InSiblingBufferBlock_UsesSyntheticStorage()
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
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister] = 0x1234_0000,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 1] = 0,
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 2] = 64,
            // Resource type 1 is not a V# buffer descriptor.
            [Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + 3] = 1u << 30,
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
        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(0UL, binding.BaseAddress);
        Assert.Equal(new uint[] { 0 }, binding.InstructionPcs);
        Assert.True(binding.Writable);
        Assert.False(binding.WriteBackToGuest);
        Assert.Equal(sizeof(uint), binding.DataLength);
    }

    [Fact]
    public void DeferredVertexDescriptor_DecodesLiveLayout()
    {
        const ulong baseAddress = 0x1234_5678_9ABC;
        const uint stride = 16;
        const uint records = 37;
        // Unified FORMAT 5 is R8_UINT (data format 1, number format 4).
        uint[] words =
        [
            unchecked((uint)baseAddress),
            unchecked((uint)(baseAddress >> 32)) | stride << 16,
            records,
            5u << 12,
        ];

        Assert.True(
            Gen5ShaderScalarEvaluator.TryDecodeDeferredVertexDescriptor(
                words,
                out var decodedAddress,
                out var decodedStride,
                out var decodedSize,
                out var dataFormat,
                out var numberFormat));
        Assert.Equal(baseAddress, decodedAddress);
        Assert.Equal(stride, decodedStride);
        Assert.Equal((ulong)stride * records, decodedSize);
        Assert.Equal(1u, dataFormat);
        Assert.Equal(4u, numberFormat);
    }

    [Fact]
    public void DeferredVertexDescriptor_RejectsNonBufferResource()
    {
        uint[] words =
        [
            0x1000,
            16u << 16,
            4,
            (5u << 12) | (1u << 30),
        ];

        Assert.False(
            Gen5ShaderScalarEvaluator.TryDecodeDeferredVertexDescriptor(
                words,
                out _,
                out _,
                out _,
                out _,
                out _));
    }
}
