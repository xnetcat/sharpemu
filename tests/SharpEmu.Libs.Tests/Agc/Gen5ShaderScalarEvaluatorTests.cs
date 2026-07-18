// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
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

    [Fact]
    public void VertexInputs_RespectMetalAttributeLimit()
    {
        const ulong vertexAddress = ShaderAddress + 0x1000;
        const uint stride = 16;
        const int expectedNativeVertexInputs = 31;
        var memory = new FakeCpuMemory(ShaderAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var instructions = Enumerable
            .Range(0, expectedNativeVertexInputs + 2)
            .Select(index =>
                new Gen5ShaderInstruction(
                    checked((uint)(index * 8)),
                    Gen5ShaderEncoding.Mubuf,
                    "BufferLoadFormatXyzw",
                    [],
                    [
                        Gen5Operand.Vector(0),
                        Gen5Operand.Scalar(0),
                        Gen5Operand.Source(128),
                    ],
                    [
                        Gen5Operand.Vector(4),
                        Gen5Operand.Vector(5),
                        Gen5Operand.Vector(6),
                        Gen5Operand.Vector(7),
                    ],
                    new Gen5BufferMemoryControl(
                        DwordCount: 4,
                        VectorAddress: 0,
                        VectorData: 4,
                        ScalarResource: 0,
                        OffsetBytes: 0,
                        IndexEnabled: true,
                        OffsetEnabled: false,
                        Glc: false,
                        Slc: false)))
            .ToArray();
        var userData = new uint[]
        {
            unchecked((uint)vertexAddress),
            unchecked((uint)(vertexAddress >> 32)) | stride << 16,
            4,
            // Unified FORMAT 77 is R32G32B32A32_FLOAT.
            77u << 12,
        };
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, instructions),
            userData,
            Metadata: null);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: 4),
            error);
        Assert.Equal(
            expectedNativeVertexInputs,
            evaluation.VertexInputs!.Count);
        Assert.Equal(
            Enumerable.Range(0, expectedNativeVertexInputs)
                .Select(static index => (uint)(index * 8)),
            evaluation.VertexInputs.Select(static input => input.Pc));

        var overflow = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(vertexAddress, overflow.BaseAddress);
        Assert.Equal(new uint[] { 31u * 8, 32u * 8 }, overflow.InstructionPcs);
    }

    [Fact]
    public void RuntimeGuestBufferLength_IncludesAlignedDescriptorBias()
    {
        var binding = new Gen5GlobalMemoryBinding(
            ScalarAddress: 0,
            BaseAddress: ShaderAddress + 3,
            InstructionPcs: [0],
            Data: new byte[5],
            DataLength: 5,
            DataPooled: false);

        Assert.Equal(
            2u,
            AgcExports.GetRuntimeGuestBufferDwordLength(binding));
    }
}
