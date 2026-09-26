// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// Coverage for the VOP1 encodings the GFX10 front end used to reject. Silent
/// Hill: The Short Message fails the whole pipeline with
/// "unknown-vop1 op=0x57" (V_LOG_F16) from one pixel shader, so every opcode in
/// the f16 unary family has to decode and emit.
/// </summary>
public sealed class Gen5Vop1CoverageTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    /// <summary>VOP1: 0b0111111 op-prefix, vdst[24:17], op[16:9], src0[8:0].</summary>
    private static uint Vop1(uint opcode, uint vdst, uint src0) =>
        0x7E00_0000u | (vdst << 17) | (opcode << 9) | src0;

    /// <summary>VOP3-encoded VOP1: the VOP3 opcode is the VOP1 opcode + 0x180.</summary>
    private static (uint Low, uint High) Vop3(
        uint vop3Opcode,
        uint vdst,
        uint src0,
        uint operandSelect = 0,
        uint absolute = 0,
        bool clamp = false,
        uint negate = 0,
        uint outputModifier = 0,
        uint src1 = 0,
        uint src2 = 0) =>
        ((0x35u << 26) |
            (vop3Opcode << 16) |
            (clamp ? 1u << 15 : 0u) |
            (operandSelect << 11) |
            (absolute << 8) |
            vdst,
        src0 | (src1 << 9) | (src2 << 18) | (outputModifier << 27) | (negate << 29));

    public static TheoryData<uint, string> Float16UnaryFamily() => new()
    {
        { 0x50, "VCvtF16U16" },
        { 0x51, "VCvtF16I16" },
        { 0x52, "VCvtU16F16" },
        { 0x53, "VCvtI16F16" },
        { 0x54, "VRcpF16" },
        { 0x55, "VSqrtF16" },
        { 0x56, "VRsqF16" },
        { 0x57, "VLogF16" },
        { 0x58, "VExpF16" },
        { 0x59, "VFrexpMantF16" },
        { 0x5A, "VFrexpExpI16F16" },
        { 0x5B, "VFloorF16" },
        { 0x5C, "VCeilF16" },
        { 0x5D, "VTruncF16" },
        { 0x5E, "VRndneF16" },
        { 0x5F, "VFractF16" },
        { 0x60, "VSinF16" },
        { 0x61, "VCosF16" },
    };

    [Theory]
    [MemberData(nameof(Float16UnaryFamily))]
    public void Float16UnaryVop1DecodesAndCompiles(uint opcode, string expectedName)
    {
        var program = Decode([Vop1(opcode, 0, 257), SEndpgm]);
        Assert.Equal(expectedName, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop1, program.Instructions[0].Encoding);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        // No native f16: every one of these runs through the software
        // widen/narrow sequences.
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void Float16ResultsPreserveTheUnselectedDestinationHalf()
    {
        // v_log_f16 v0, v1 must leave v0[31:16] alone: the emitted sequence masks
        // the destination with 0xFFFF0000 before OR-ing the narrowed result in.
        var program = Decode([Vop1(0x57, 0, 257), SEndpgm]);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Assert.Contains(0xFFFF_0000u, ReadUIntConstants(shader.Spirv));
    }

    [Fact]
    public void Float16UnaryAcceptsInlineFloatConstantSource()
    {
        // 0xF0 is the inline constant 0.5. An f16 operand must read it as the
        // value 0.5, not as an f16 bit pattern (which would decode to zero).
        var program = Decode([Vop1(0x55, 0, 0xF0), SEndpgm]);
        Assert.Equal("VSqrtF16", program.Instructions[0].Opcode);
        Assert.Equal(
            Gen5OperandKind.EncodedConstant,
            program.Instructions[0].Sources[0].Kind);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        // 0.5f, not 0x3800 reinterpreted and not zero.
        Assert.Contains(0x3F00_0000u, ReadUIntConstants(shader.Spirv));
    }

    internal static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    internal static bool TryDecode(
        IReadOnlyList<uint> words,
        out Gen5ShaderProgram program,
        out string error)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        return Gen5ShaderTranslator.TryDecodeProgram(
            context,
            ShaderAddress,
            out program,
            out error);
    }

    private static IReadOnlyList<ushort> ReadCapabilities(byte[] spirv) =>
        ReadInstructions(spirv)
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Capability)
            .Select(instruction => (ushort)instruction.Operands[0])
            .ToArray();

    /// <summary>Every OpConstant literal in the module (result type ignored).</summary>
    private static IReadOnlyList<uint> ReadUIntConstants(byte[] spirv) =>
        ReadInstructions(spirv)
            .Where(instruction =>
                instruction.Opcode == (ushort)SpirvOp.Constant &&
                instruction.Operands.Count >= 3)
            .Select(instruction => instruction.Operands[2])
            .ToArray();

    private static IReadOnlyList<(ushort Opcode, IReadOnlyList<uint> Operands)> ReadInstructions(
        byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var instructions = new List<(ushort, IReadOnlyList<uint>)>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint))));
            }

            instructions.Add(((ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    /// <summary>
    /// Runs spirv-val when it is installed (VULKAN_SDK, else PATH). The new
    /// sequences mix int and float domains, so structural validation is worth
    /// more here than opcode spotting.
    /// </summary>
    internal static void ValidateWhenAvailable(byte[] code)
    {
        var executable = FindSpirvVal();
        if (executable is null)
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindSpirvVal()
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var name = OperatingSystem.IsWindows() ? "spirv-val.exe" : "spirv-val";
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            var candidate = Path.Combine(
                sdk,
                OperatingSystem.IsWindows() ? "Bin" : "bin",
                name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
