// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5CubeTextureTests
{
    [Fact]
    public void CubeShapeMapsToSixLayer2DArray()
    {
        // A decoded cube's Depth is already the face-layer count (RDNA2
        // word4 stores the last cube-map array slice, so five denotes six
        // image layers). Cubes render as 2D arrays, never as 3D volumes.
        var shape = VulkanVideoPresenter.GetGuestTextureShape(
            type: 11,
            depth: 6);
        Assert.False(shape.Is3D);
        Assert.True(shape.IsLayered);
        Assert.Equal(1u, shape.VolumeDepth);
        Assert.Equal(6u, shape.ArrayLayers);
    }

    [Fact]
    public void VolumeShapeKeepsDepthAndSingleLayer()
    {
        var shape = VulkanVideoPresenter.GetGuestTextureShape(
            type: 10,
            depth: 33);
        Assert.True(shape.Is3D);
        Assert.False(shape.IsLayered);
        Assert.Equal(33u, shape.VolumeDepth);
        Assert.Equal(1u, shape.ArrayLayers);
    }

    [Fact]
    public void CubeSampleLowersToLayered2DImageAndRemapsFaceId()
    {
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: [0, 1, 2],
            VectorData: 4,
            ScalarResource: 0,
            ScalarSampler: 8,
            Dimension: 3,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            "ImageSampleLz",
            [],
            [],
            [Gen5Operand.Vector(4)],
            control);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x4000, [instruction]),
            [],
            Metadata: null);
        var binding = new Gen5ImageBinding(
            0,
            instruction.Opcode,
            control,
            [
                0x00000010,
                0x03800000,
                0x00000000,
                0xB0000F2E,
                0x00000005,
                0x00700000,
                0x00000000,
                0x00000000,
            ],
            [0u, 0u, 0u, 0u],
            MipLevel: null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            [binding],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        var instructions = ReadInstructions(shader.Spirv);
        Assert.Contains(
            instructions,
            static instruction =>
                instruction.Opcode == SpirvOp.TypeImage &&
                instruction.Words.Length >= 9 &&
                instruction.Words[3] == (uint)SpirvImageDim.Dim2D &&
                instruction.Words[5] == 1);
        Assert.Contains(
            instructions,
            static instruction => instruction.Opcode == SpirvOp.FSub);
        Assert.Contains(
            instructions,
            static instruction => instruction.Opcode == SpirvOp.IMul);
        Assert.Contains(
            instructions,
            static instruction => instruction.Opcode == SpirvOp.ConvertFToU);
        Assert.Contains(
            instructions,
            static instruction => instruction.Opcode == SpirvOp.ConvertUToF);
    }

    private static IReadOnlyList<SpirvInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<SpirvInstruction>();
        for (var offset = 5 * sizeof(uint);
             offset + sizeof(uint) <= spirv.Length;)
        {
            var firstWord = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(firstWord >> 16), 1);
            var words = new uint[wordCount];
            for (var index = 0; index < wordCount; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(
                        offset + index * sizeof(uint),
                        sizeof(uint)));
            }

            instructions.Add(new SpirvInstruction((SpirvOp)(ushort)firstWord, words));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private readonly record struct SpirvInstruction(
        SpirvOp Opcode,
        uint[] Words);
}
