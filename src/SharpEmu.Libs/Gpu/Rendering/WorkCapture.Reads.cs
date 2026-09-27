// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Rendering;

// The reads the graphics path makes while it resolves a draw. Enumerating the tables a program
// reaches through its user scalars - the vertex attribute and buffer tables, the input semantics, the
// fetch shader, the resource tables behind a flattened binding - means keeping a second copy of the
// resolver's knowledge and losing a case to the first table nobody thought of. Recording the reads
// themselves cannot fall behind the resolver, so that is what this does.
//
// Two logs, because the reads happen at two different rates:
//   * the draw log, cleared when a draw or dispatch starts resolving, holds the reads that happen
//     per draw: the vertex input tables and the pixel interpolator semantics;
//   * the shader log, kept per shader code address, holds the reads of preparing that program -
//     decode, resource plan, specialization - which only happen the first time the program is seen,
//     usually several draws before the one a selector matches.
internal static partial class WorkCapture
{
    private const int MaxLoggedReads = 1 << 16;

    [ThreadStatic]
    private static List<GuestRead>? _drawReads;

    [ThreadStatic]
    private static List<GuestRead>? _shaderReads;

    private static readonly ConcurrentDictionary<ulong, GuestRead[]> ShaderReadsByAddress = new();

    private readonly record struct GuestRead(ulong Address, ulong Size);

    // Wraps the memory the graphics path reads guest data through; only reads inside a log are kept.
    public static ICpuMemory Recording(ICpuMemory memory) => Enabled ? new WorkCaptureMemory(memory) : memory;

    public static CpuContext RecordingContext(CpuContext context) =>
        Enabled ? new CpuContext(Recording(context.Memory), context.TargetGeneration) : context;

    // The host's word reader, with every word it hands back recorded.
    public static GuestWordReader Recording(GuestWordReader reader)
    {
        if (!Enabled)
        {
            return reader;
        }

        return (ulong address, out uint word) =>
        {
            var read = reader(address, out word);
            if (read)
            {
                NoteRead(address, sizeof(uint));
            }

            return read;
        };
    }

    // Starts the draw log: the reads of resolving one draw or dispatch replace the last one's.
    public static void BeginResolution()
    {
        if (!Enabled)
        {
            return;
        }

        (_drawReads ??= []).Clear();
    }

    // Opens the log of one program's preparation; disposal merges it into that program's ranges.
    public static ShaderReadScope BeginShaderReads(ulong codeAddress) => new(codeAddress);

    public static void NoteRead(ulong address, int size)
    {
        if (address == 0 || size <= 0)
        {
            return;
        }

        Append(_drawReads, address, (ulong)size);
        Append(_shaderReads, address, (ulong)size);
    }

    // Consecutive reads of one table coalesce as they arrive, so a table costs one entry, not one per word.
    private static void Append(List<GuestRead>? log, ulong address, ulong size)
    {
        if (log is null)
        {
            return;
        }

        if (log.Count != 0)
        {
            var last = log[^1];
            var end = last.Address + last.Size;
            if (address >= last.Address && address <= end)
            {
                log[^1] = last with { Size = Math.Max(end, address + size) - last.Address };
                return;
            }
        }

        if (log.Count >= MaxLoggedReads)
        {
            return;
        }

        log.Add(new GuestRead(address, size));
    }

    // The reads of resolving the draw or dispatch that is being captured.
    public static IEnumerable<(ulong Address, ulong Size)> ResolutionReads() =>
        _drawReads is null ? [] : _drawReads.Select(static read => (read.Address, read.Size)).ToArray();

    // The reads of preparing the program at a code address, from whichever earlier draw first saw it.
    public static IEnumerable<(ulong Address, ulong Size)> ShaderReads(ulong codeAddress) =>
        ShaderReadsByAddress.TryGetValue(codeAddress, out var reads)
            ? reads.Select(static read => (read.Address, read.Size)).ToArray()
            : [];

    internal readonly struct ShaderReadScope : IDisposable
    {
        private readonly ulong _codeAddress;
        private readonly List<GuestRead>? _previous;

        public ShaderReadScope(ulong codeAddress)
        {
            if (!Enabled)
            {
                _codeAddress = 0;
                _previous = null;
                return;
            }

            _codeAddress = codeAddress;
            _previous = _shaderReads;
            _shaderReads = [];
        }

        public void Dispose()
        {
            if (!Enabled)
            {
                return;
            }

            var reads = _shaderReads;
            _shaderReads = _previous;
            if (reads is null || reads.Count == 0 || _codeAddress == 0)
            {
                return;
            }

            // A program prepared twice reads the same tables; the longer log wins over a partial one.
            ShaderReadsByAddress.AddOrUpdate(
                _codeAddress,
                _ => [.. reads],
                (_, existing) => existing.Length >= reads.Count ? existing : [.. reads]);
        }
    }
}

// Forwards every call and records the ranges a read covered. Wrappers unwrap through Inner, so the
// translator's per-memory registries still see one guest memory.
internal sealed class WorkCaptureMemory(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
{
    public ICpuMemory Inner => inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!inner.TryRead(virtualAddress, destination))
        {
            return false;
        }

        WorkCapture.NoteRead(virtualAddress, destination.Length);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => inner.TryWrite(virtualAddress, source);

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) => inner.TryCompare(virtualAddress, expected);

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected, out bool equal) =>
        inner.TryCompare(virtualAddress, expected, out equal);

    public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length) =>
        inner.TryCopy(destinationAddress, sourceAddress, length);

    public bool CanRead(ulong address, ulong size) => inner.CanRead(address, size);

    public string DescribeReadRange(ulong address, ulong size) => inner.DescribeReadRange(address, size);
}
