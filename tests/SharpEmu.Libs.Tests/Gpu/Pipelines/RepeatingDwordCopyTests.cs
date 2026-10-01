// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class RepeatingDwordCopyTests
{
    internal static Gen5ShaderProgram DecodeKernel()
    {
        var memory = new FakeCpuMemory(0x1000, 1024);
        Assert.True(memory.TryWrite(0x1000, MemoryMarshal.AsBytes(ShaderPipelineCache.RepeatingCopyWords)));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), 0x1000,
            out var program, out var error), error);
        return program;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void PipelineUsesCommandDimensionsAndRequiresResidentInputs(bool threadDimensions, bool resident)
    {
        var guest = new PipelineTestGuest();
        const ulong code = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong header = PipelineTestGuest.MemoryBase + 0x2000;
        const ulong source = PipelineTestGuest.MemoryBase + 0x3000;
        const ulong destination = PipelineTestGuest.MemoryBase + 0x4000;
        const ulong control = PipelineTestGuest.MemoryBase + 0x5000;
        guest.RegisterProgram(code, header, ShaderPipelineCache.RepeatingCopyWords.ToArray());
        guest.WriteWords(source, 11, 22, 33);
        guest.WriteWords(destination, Enumerable.Repeat(0xCDCDCDCDu, 192).ToArray());
        guest.WriteWords(control, 152, 3, 0, 0);
        guest.Host.ResidentReadsEnabled = resident;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry, repeatingCopyEnabled: true);
        var compute = new ComputeStageRegisters
        {
            Address = code, ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1,
            UserScalarCount = 12, ThreadGroupIdXEnable = true,
        };
        uint[] descriptors = [.. PipelineTestGuest.BufferDescriptor(source, 4, 3, 20),
            .. PipelineTestGuest.BufferDescriptor(destination, 4, 192, 20),
            .. PipelineTestGuest.BufferDescriptor(control, 16, 1, 20)];
        descriptors.CopyTo(compute.UserScalars.Values, 0);
        var result = cache.GetComputeProgram(compute, new ShaderInterfaceRegisters(),
            threadDimensions ? 1u << 5 : 0, threadDimensions ? 130u : 3u, 1, 1);
        Assert.Equal(resident, result.Consumed);
        var bytes = new byte[192 * 4];
        Assert.True(guest.Memory.TryRead(destination, bytes));
        var output = MemoryMarshal.Cast<byte, uint>(bytes);
        var count = resident ? (threadDimensions ? 130 : 152) : 0;
        for (var index = 0; index < output.Length; index++)
            Assert.Equal(index < count ? (uint)((index % 3 + 1) * 11) : 0xCDCDCDCDu, output[index]);
        if (resident) Assert.Empty(guest.Compiler.Requests);
        else Assert.Single(guest.Compiler.Requests);
    }

    [Fact]
    public void RecognitionRequiresEveryExecutableWord()
    {
        var program = DecodeKernel();
        Assert.True(ShaderPipelineCache.IsRepeatingDwordCopyKernel(program));
        var wordIndex = 0;
        for (var index = 0; index < program.Instructions.Count; index++)
            for (var word = 0; word < program.Instructions[index].Words.Count; word++, wordIndex++)
            {
                var instructions = program.Instructions.ToArray();
                var words = instructions[index].Words.ToArray();
                words[word] ^= 1;
                instructions[index] = instructions[index] with { Words = words };
                Assert.Equal(wordIndex == 1,
                    ShaderPipelineCache.IsRepeatingDwordCopyKernel(program with { Instructions = instructions }));
            }
        Assert.False(ShaderPipelineCache.IsRepeatingDwordCopyKernel(program with { Instructions = program.Instructions.Skip(1).ToArray() }));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(7u)]
    [InlineData(19u)]
    [InlineData(uint.MaxValue)]
    public void RepeatingCopyPreservesSnapshotAndBounds(uint period)
    {
        var memory = new FakeCpuMemory(0x1000, 128);
        Assert.True(memory.TryWrite(0x1000, Enumerable.Repeat((byte)0xCD, 128).ToArray()));
        uint[] input = [11, 22, 33];
        Assert.True(memory.TryWrite(0x1000, MemoryMarshal.AsBytes(input.AsSpan())));
        Assert.True(ShaderPipelineCache.TryCopyRepeatingDwords(memory, 0x1000, 12, 0x1004, 19, period));
        var actual = new byte[128];
        Assert.True(memory.TryRead(0x1000, actual));
        for (uint index = 0; index < 19; index++)
            Assert.Equal(index % period < input.Length ? input[index % period] : 0,
                MemoryMarshal.Read<uint>(actual.AsSpan(4 + (int)index * 4)));
        Assert.Equal(11u, MemoryMarshal.Read<uint>(actual));
        Assert.All(actual[80..], value => Assert.Equal(0xCD, value));
    }

    [Fact]
    public void GpuOwnedSourceAndZeroDivisorDeclineWithoutWriting()
    {
        var memory = new FakeCpuMemory(0x1000, 128);
        var initial = Enumerable.Repeat((byte)0xAB, 128).ToArray();
        Assert.True(memory.TryWrite(0x1000, initial));
        static bool Refuse(ulong address, Span<byte> bytes, bool clean) => false;
        Assert.False(ShaderPipelineCache.TryCopyRepeatingDwords(memory, 0x1000, 4, 0x1040, 16, 1, Refuse));
        Assert.False(ShaderPipelineCache.TryCopyRepeatingDwords(memory, 0x1000, 4, 0x1040, 16, 0));
        Assert.False(ShaderPipelineCache.TryCopyRepeatingDwords(memory, 0x1000, 4, 0x1040, uint.MaxValue, 1));
        var actual = new byte[128];
        Assert.True(memory.TryRead(0x1000, actual));
        Assert.Equal(initial, actual);
    }
}
