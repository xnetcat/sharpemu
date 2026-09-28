// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The binary information block a compiled shader can carry after its first two words.
public readonly record struct ShaderBinaryInfo(uint Hash0, uint Hash1)
{
    public const uint MarkerWord = 0xBEEB03FF;
    public const int ByteSize = 28;

    public ulong DeclaredHash => ((ulong)Hash1 << 32) | Hash0;
}

// A program is identified by its content hash, never by its address.
public static class ShaderIdentity
{
    // The declared hash from the marker block, or zero when the code carries none.
    public static bool TryReadDeclaredHash(ICpuMemory memory, ulong codeAddress, out ulong declaredHash)
    {
        declaredHash = 0;
        Span<byte> head = stackalloc byte[2 * sizeof(uint)];
        if (!memory.TryRead(codeAddress, head))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != ShaderBinaryInfo.MarkerWord)
        {
            return true;
        }

        var infoAddress = codeAddress + ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + 1) * 2 * sizeof(uint);
        Span<byte> info = stackalloc byte[ShaderBinaryInfo.ByteSize];
        if (!memory.TryRead(infoAddress, info))
        {
            return false;
        }

        declaredHash = new ShaderBinaryInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(info[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(info[20..])).DeclaredHash;
        return true;
    }

    // The declared hash when it is not zero, else the content hash of every code range.
    [ThreadStatic]
    private static XxHash3? _hash;

    public static ulong Compute(ICpuMemory memory, ulong codeAddress, ReadOnlySpan<(ulong Address, uint SizeBytes)> ranges, string label)
    {
        if (!TryReadDeclaredHash(memory, codeAddress, out var declaredHash))
        {
            throw Scheduling.SubmissionScheduler.Fatal($"The shader code is unreadable: label={label} shader=0x{codeAddress:X16}.");
        }

        if (declaredHash != 0)
        {
            return declaredHash;
        }

        // Runs per draw for shaders without a declared hash: the hasher and code buffer are reused.
        var hash = _hash ??= new XxHash3();
        hash.Reset();
        foreach (var (address, sizeBytes) in ranges)
        {
            var code = ArrayPool<byte>.Shared.Rent((int)sizeBytes);
            try
            {
                var span = code.AsSpan(0, (int)sizeBytes);
                if (!memory.TryRead(address, span))
                {
                    throw Scheduling.SubmissionScheduler.Fatal($"The shader code is unreadable: label={label} shader=0x{address:X16} size=0x{sizeBytes:X8}.");
                }

                hash.Append(span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(code);
            }
        }

        return hash.GetCurrentHashAsUInt64();
    }
}
