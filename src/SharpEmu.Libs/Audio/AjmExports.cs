// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int InvalidParameter = unchecked((int)0x806A0001);
    private const uint MaximumGen5InitializeRevision = 3;

    private static readonly ConcurrentDictionary<uint, byte> Contexts = new();
    private static int _nextContextId;

    public static int AjmInitialize(CpuContext ctx)
    {
        var initializeFlags = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!IsValidInitializeFlags(ctx.TargetGeneration, initializeFlags) || outputAddress == 0)
        {
            return InvalidParameter;
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return InvalidParameter;
        }

        Contexts[contextId] = 0;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.initialize flags=0x{initializeFlags:X16} " +
                $"revision={initializeFlags >> 32} out=0x{outputAddress:X16} context={contextId}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    internal static bool IsValidInitializeFlags(Generation generation, ulong initializeFlags)
    {
        // Gen4 defines this argument as an s64 reserved value and requires
        // zero. Gen5 retained the same NID and output pointer but encodes the
        // AJM ABI revision in the high dword. Current SDK middleware passes
        // 0x00000003_00000000; the low reserved dword must remain zero.
        if (initializeFlags == 0)
        {
            return true;
        }

        var revision = initializeFlags >> 32;
        return generation == Generation.Gen5 &&
               (initializeFlags & uint.MaxValue) == 0 &&
               revision is >= 1 and <= MaximumGen5InitializeRevision;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (reserved != 0 || !Contexts.ContainsKey(contextId))
        {
            return InvalidParameter;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.module_register context={contextId} codec={codecType} reserved={reserved}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // The caller owns and initializes the batch storage. This API resets
        // its submission cursor on hardware; FMOD does not consume a return
        // value or an additional output object here.
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
