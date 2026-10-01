// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Borrows the render thread's scratch words for lookup; only inserted keys own a copy.
internal readonly ref struct ProgramKeyLookup(
    ShaderStage stage, ulong hash, uint userDataCount, uint codeSize, ReadOnlySpan<uint> staticState)
{
    public ShaderStage Stage { get; } = stage;
    public ulong Hash { get; } = hash;
    public uint UserDataCount { get; } = userDataCount;
    public uint CodeSize { get; } = codeSize;
    public ReadOnlySpan<uint> StaticState { get; } = staticState;
}

internal sealed class ProgramKeyComparer : IEqualityComparer<ProgramKey>, IAlternateEqualityComparer<ProgramKeyLookup, ProgramKey>
{
    public static readonly ProgramKeyComparer Instance = new();
    public bool Equals(ProgramKey? left, ProgramKey? right) => left is null ? right is null : left.Equals(right);
    public int GetHashCode(ProgramKey key) => key.GetHashCode();
    public bool Equals(ProgramKeyLookup lookup, ProgramKey key) =>
        lookup.Stage == key.Stage && lookup.Hash == key.Hash && lookup.UserDataCount == key.UserDataCount &&
        lookup.CodeSize == key.CodeSize && lookup.StaticState.SequenceEqual(key.StaticState);
    public int GetHashCode(ProgramKeyLookup lookup) =>
        HashCode.Combine(lookup.Stage, lookup.Hash, lookup.UserDataCount, lookup.CodeSize, lookup.StaticState.Length);
    public ProgramKey Create(ProgramKeyLookup lookup) =>
        new(lookup.Stage, lookup.Hash, lookup.UserDataCount, lookup.CodeSize, lookup.StaticState.ToArray());
}
