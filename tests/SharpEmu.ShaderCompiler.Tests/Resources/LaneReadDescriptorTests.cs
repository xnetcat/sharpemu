// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class LaneReadDescriptorTests
{
    [Fact]
    public void DynamicLaneReadsRetainInstructionIdentityAndAreNotCpuValues()
    {
        var graph = ScalarValueGraph.Build(Program(EndProgram(0)), 0, 2);
        var first = graph.LaneRead(graph.UserData(0), graph.UserData(1), 4);
        var second = graph.LaneRead(graph.UserData(0), graph.UserData(1), 8);
        Assert.Same(first, graph.LaneRead(graph.UserData(0), graph.UserData(1), 4));
        Assert.False(graph.Equivalent(first, second));
        Assert.False(new RuntimeValueValidator(graph, 0, 2, 0).Validate(first));
    }

    private static Gen5ShaderProgram CreateProgram() => Shift(Program(
        ScalarLoad(0, 46, 16, immediateOffset: 0x80),
        Sop1(8, "SFF1I32B32", 18, Gen5Operand.Scalar(16)),
        Sop2(12, "SMulI32", 106, Gen5Operand.Scalar(18), Operand(0x90)),
        MoveVectorFromScalar(16, 34, 18),
        Vop2(20, "VLshlrevB32", 15, Operand(4), Gen5Operand.Vector(34)),
        Vop3(24, "VLshlAddU32", 15, Gen5Operand.Vector(15), Operand(3), Gen5Operand.Vector(15)),
        Sop2(32, "SLshlB32", 19, Operand(1), Gen5Operand.Scalar(18)),
        Sop2(36, "SXorB32", 16, Gen5Operand.Scalar(19), Gen5Operand.Scalar(16)),
        Vop2(40, "VAddI32", 15, Operand(0x40), Gen5Operand.Vector(15)),
        GlobalMemory(44, "GlobalLoadDword", 46, 15, 22, 0),
        Sop1(52, "SMovB64", 12, Gen5Operand.Scalar(126)),
        Sop1(56, "SFF1I32B64", 0, Gen5Operand.Scalar(12)),
        new(60, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
            [Gen5Operand.Vector(22), Gen5Operand.Scalar(0), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(106)], null),
        new(68, Gen5ShaderEncoding.Vopc, "VCmpEqU32", [0u, 0u],
            [Gen5Operand.Scalar(106), Gen5Operand.Vector(22)], [Gen5Operand.Scalar(14)], null),
        Sop1(76, "SAndSaveexecB64", 20, Gen5Operand.Scalar(14)),
        Branch(80, "SCbranchExecz", 8),
        Sop2(84, "SLshlB32", 106, Gen5Operand.Scalar(106), Operand(5)),
        ScalarLoad(88, 46, 4, 4, immediateOffset: 0x100, dynamicOffsetRegister: 106),
        Sop2(96, "SAddI32", 107, Gen5Operand.Scalar(106), Operand(16)),
        ScalarLoad(100, 46, 8, 4, immediateOffset: 0x100, dynamicOffsetRegister: 107),
        Image(108, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
        Sop2(116, "SAndn2B64", 12, Gen5Operand.Scalar(12), Gen5Operand.Scalar(14)),
        Sop1(120, "SMovB64", 126, Gen5Operand.Scalar(20)),
        Branch(124, "SCbranchScc1", -18),
        EndProgram(128)));

    private static Gen5ShaderProgram Shift(Gen5ShaderProgram program) => Program(
        [MoveScalarRegister(0, 46, 0), MoveScalarRegister(4, 47, 1),
         .. program.Instructions.Select(instruction => instruction with { Pc = instruction.Pc + 8 })]);

    [Fact]
    public void RemainingLaneLoopWithSplitDescriptorLoadsUsesBoundedImageTable()
    {
        var plan = Extract(CreateProgram(), userDataCount: 2);
        var selector = Assert.Single(plan.DescriptorSources.Where(source => source.IndirectImage is not null)).IndirectImage!;
        Assert.Equal(new WaveIndexedImageSelector(0x80, 0x40, 0x90), selector.WaveIndexed);
        Assert.Equal(0x100u, selector.TableOffset);
        Assert.Equal(0u, selector.KeyBound);
    }

    [Fact]
    public void LaneReadMaterializesOnlyReachableKeysAndRejectsMissingCleanData()
    {
        var plan = Extract(CreateProgram(), userDataCount: 2);
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address == 0x1080) { word = (1u << 1) | (1u << 4); return true; }
            if (address == 0x1040 + 0x90) { word = 2; return true; }
            if (address == 0x1040 + 4 * 0x90) { word = 5; return true; }
            if (address < 0x1100 || address >= 0x1100 + 6 * 32) return false;
            var relative = address - 0x1100;
            var record = relative / 32;
            if (record is not (2 or 5)) return false;
            word = (relative % 32 / 4) switch
            {
                0 => record == 2 ? 0x2000u : 0x1000u,
                1 => 20u << 20,
                3 => 0xFACu | (9u << 28),
                _ => 0,
            };
            return true;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readMemory: Read, readCleanMemory: Read), ref snapshot, ref specialization));
        Assert.Equal(2, snapshot.Images.Length);
        snapshot = new();
        specialization = new();
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readMemory: Read), ref snapshot, ref specialization));
    }

    [Theory]
    [InlineData(13u, false)] // High half of the remaining lane mask.
    [InlineData(15u, false)] // High half of the matching lane mask.
    [InlineData(21u, false)] // High half of saved EXEC.
    [InlineData(127u, false)] // High half of EXEC itself.
    [InlineData(100u, true)] // Unrelated body writes remain supported.
    public void BodyWritesMustPreserveBothHalvesOfProtectedMasks(uint register, bool allowed)
    {
        var original = CreateProgram();
        var instructions = original.Instructions.Select(instruction => instruction.Pc switch
        {
            88 => Branch(88, "SCbranchExecz", 9),
            132 => Branch(136, "SCbranchScc1", -19),
            >= 124 => instruction with { Pc = instruction.Pc + 4 },
            _ => instruction,
        }).ToList();
        instructions.Insert(instructions.FindIndex(instruction => instruction.Pc == 128),
            MoveScalar(124, register, 0));
        var changed = Program(instructions.ToArray());
        if (allowed)
            Assert.Contains(Extract(changed, userDataCount: 2).DescriptorSources, source => source.IndirectImage is not null);
        else
            AssertStrictRejection(changed);
    }

    [Theory]
    [InlineData(32u)] // A different bit is not the mask's selected bit.
    [InlineData(52u)] // The remaining mask must start with EXEC.
    [InlineData(56u)] // The lane must be the first remaining lane.
    [InlineData(68u)] // The executed lanes must have the same key.
    [InlineData(116u)] // Remove exactly the lanes just processed.
    [InlineData(120u)] // Restore the saved EXEC before iterating.
    public void UnprovedLaneSelectionDoesNotBecomeAnIndirectTable(uint changedPc)
    {
        var original = CreateProgram();
        var changed = new Gen5ShaderProgram(original.Address, original.Instructions.Select(instruction =>
            instruction.Pc == changedPc + 8 ? Nop(changedPc + 8) with { Words = instruction.Words } : instruction).ToArray());
        AssertStrictRejection(changed);
    }

    private static void AssertStrictRejection(Gen5ShaderProgram changed)
    {
        try
        {
            var plan = Extract(changed, userDataCount: 2);
            Assert.DoesNotContain(plan.DescriptorSources, source => source.IndirectImage is not null);
        }
        catch (ResourcePlanException) { } // Existing strict rejection is also safe.
    }
}
