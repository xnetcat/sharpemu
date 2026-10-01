// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelInputMappingTests
{
    [Fact]
    public void DrawMappingReusesDecodedInterfaceWithoutAllocating()
    {
        static Gen5ShaderInstruction Interpolation(uint attribute) => new(0, Gen5ShaderEncoding.Vintrp,
            "VInterpP1F32", [0], [], [], new Gen5InterpolationControl(attribute, 0));
        var program = new Gen5ShaderProgram(0, [Interpolation(2), Interpolation(0), Interpolation(2), Interpolation(1)]);
        Assert.Equal([0u, 1u, 2u], program.InterpolatedAttributes.ToArray());
        Assert.Equal(3u, program.InterpolatedAttributeCount);
        uint[] controls = [1, 1, 2];
        uint[] output = [99, 99, 99, 99];
        Gen5PixelInputMapping.ResolveLocations(controls, program.InterpolatedAttributes, output);
        Assert.Equal([1u, 2u, 3u, 99u], output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
            Gen5PixelInputMapping.ResolveLocations(controls, program.InterpolatedAttributes, output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ShortOutputIsRejectedBeforeWriting()
    {
        uint[] output = [99];
        Assert.Throws<ArgumentException>(() => Gen5PixelInputMapping.ResolveLocations([0, 1], [0, 1], output));
        Assert.Equal(99u, output[0]);
    }

    [Fact]
    public void ResolveLocations_PreservesUniqueMappings()
    {
        uint[] controls = [5, 7, 9];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [5u, 7u, 9u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RelocatesDuplicateMappings()
    {
        uint[] controls = [0, 0, 0];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [0u, 1u, 2u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_SkipsOccupiedFallbackLocations()
    {
        uint[] controls = [1, 1, 2];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [1u, 2u, 3u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_UsesIdentityBeyondProgrammedControls()
    {
        uint[] controls = [0x407];
        uint[] activeInputs = [0, 2, 5];

        Assert.Equal(
            [7u, 2u, 5u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RejectsFallbackPastLastLocation()
    {
        var controls = new uint[32];
        controls[0] = 31;
        controls[31] = 31;
        uint[] activeInputs = [0, 31];

        Assert.Throws<InvalidOperationException>(() =>
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }
}
