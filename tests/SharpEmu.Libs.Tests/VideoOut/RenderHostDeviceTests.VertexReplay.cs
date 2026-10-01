// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(0x7FC00000u)] // NaN payloads must survive the interface unchanged.
    [InlineData(0xFFFF0000u)]
    [InlineData(0x3C003C00u)] // Packed half-floats.
    [InlineData(0u)] // Subnormal bit patterns.
    public void VertexReplayPreservesPackedWordsFromEveryCorner(uint pattern)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, 0,
            interpolationShader: CompileReplayProbe(ShaderStage.Pixel, pattern),
            replayVertexShader: CompileReplayProbe(ShaderStage.Vertex, pattern));
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(0xFF030201u, Pixel(pixels, Size / 2, Size / 2));
        Assert.Equal(0xFF030201u, Pixel(pixels, 1, 1));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    private static byte[] CompileReplayProbe(ShaderStage stage, uint pattern)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint Pc() => (uint)instructions.Count * 4;
        void Unary(string opcode, uint destination, Gen5Operand source) => instructions.Add(Vop1(Pc(), opcode, destination, source));
        void Binary(string opcode, uint destination, Gen5Operand left, Gen5Operand right) => instructions.Add(Vop2(Pc(), opcode, destination, left, right));
        void Export(uint target, params uint[] registers) => instructions.Add(new Gen5ShaderInstruction(Pc(), Gen5ShaderEncoding.Exp,
            "Exp", [], registers.Select(Gen5Operand.Vector).ToArray(), [], new Gen5ExportControl(target, 15, false, true, true)));
        if (stage == ShaderStage.Vertex)
        {
            Binary("VAndB32", 0, Operand(1), Gen5Operand.Vector(5));
            Binary("VLshrrevB32", 1, Operand(1), Gen5Operand.Vector(5));
            foreach (var register in new uint[] { 0, 1 })
            {
                Unary("VCvtF32U32", register, Gen5Operand.Vector(register));
                Binary("VMulF32", register, Operand(0x40800000), Gen5Operand.Vector(register));
                Binary("VAddF32", register, Operand(0xBF800000), Gen5Operand.Vector(register));
            }
            Unary("VMovB32", 2, Operand(0));
            Unary("VMovB32", 3, Operand(0x3F800000));
            Export(12, 0, 1, 2, 3);
            Binary("VAddU32", 4, Operand(1), Gen5Operand.Vector(5));
            Binary("VOrB32", 4, Operand(pattern), Gen5Operand.Vector(4));
            Export(32, 4, 4, 4, 4);
        }
        else
        {
            for (uint vertex = 0; vertex < 3; vertex++)
            {
                var selector = (vertex + 2) % 3;
                instructions.Add(new Gen5ShaderInstruction(Pc(), Gen5ShaderEncoding.Vintrp, "VInterpMovF32", [selector],
                    [Gen5Operand.Vector(selector)], [Gen5Operand.Vector(4 + vertex)], new Gen5InterpolationControl(0, 0)));
                Binary("VXorB32", 4 + vertex, Operand(pattern), Gen5Operand.Vector(4 + vertex));
                Unary("VCvtF32U32", 4 + vertex, Gen5Operand.Vector(4 + vertex));
                Binary("VMulF32", 4 + vertex, Operand(BitConverter.SingleToUInt32Bits(1f / 255)), Gen5Operand.Vector(4 + vertex));
            }
            Unary("VMovB32", 7, Operand(0x3F800000));
            Export(0, 4, 5, 6, 7);
        }
        instructions.Add(EndProgram(Pc()));
        var (plan, resources, layout) = Prepare(Program([.. instructions]), stage, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexReplayParameters = [new(0, 1)],
            SupportsPerVertexPixelInputs = false,
            PixelInputCntl = [0],
            PixelCustomInterpolationMask = 1,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = false,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }
}
