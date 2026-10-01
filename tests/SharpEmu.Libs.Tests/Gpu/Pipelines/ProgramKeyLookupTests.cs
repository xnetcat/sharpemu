// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ProgramKeyLookupTests
{
    [Fact]
    public void LookupChecksEveryIdentityFieldAndOwnsInsertedWords()
    {
        var keys = new Dictionary<ProgramKey, int>(ProgramKeyComparer.Instance);
        var alternate = keys.GetAlternateLookup<ProgramKeyLookup>();
        uint[] words = [1, 2, 3];
        alternate[new(ShaderStage.Vertex, 7, 8, 9, words)] = 42;
        words[1] = 4;
        Assert.False(alternate.ContainsKey(new(ShaderStage.Vertex, 7, 8, 9, words)));
        words[1] = 2;
        Assert.True(alternate.TryGetValue(new(ShaderStage.Vertex, 7, 8, 9, words), out var stored, out var value));
        Assert.Equal(42, value);
        Assert.NotSame(words, stored!.StaticState);
        Assert.False(alternate.ContainsKey(new(ShaderStage.Pixel, 7, 8, 9, words)));
        Assert.False(alternate.ContainsKey(new(ShaderStage.Vertex, 8, 8, 9, words)));
        Assert.False(alternate.ContainsKey(new(ShaderStage.Vertex, 7, 9, 9, words)));
        Assert.False(alternate.ContainsKey(new(ShaderStage.Vertex, 7, 8, 10, words)));
        Assert.False(alternate.ContainsKey(new(ShaderStage.Vertex, 7, 8, 9, words.AsSpan(1))));
        alternate[new(ShaderStage.Vertex, 7, 8, 9, new uint[] { 1, 4, 3 })] = 43;
        Assert.Equal(2, keys.Count); // Same hash bucket, different exact static state.
        Assert.Equal(42, alternate[new(ShaderStage.Vertex, 7, 8, 9, words)]);
    }

    [Fact]
    public void RepeatedCacheHitsAllocateNoKeyObjectsOrWordArrays()
    {
        var keys = new Dictionary<ProgramKey, int>(ProgramKeyComparer.Instance);
        var alternate = keys.GetAlternateLookup<ProgramKeyLookup>();
        uint[] words = new uint[128];
        var lookup = new ProgramKeyLookup(ShaderStage.Compute, 7, 8, 9, words);
        alternate[lookup] = 42;
        for (var i = 0; i < 1000; i++) alternate.TryGetValue(lookup, out _, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 10000; i++)
            if (alternate.TryGetValue(lookup, out _, out var value)) sum += value;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(420000, sum);
        Assert.Equal(0, allocated);
    }
}
