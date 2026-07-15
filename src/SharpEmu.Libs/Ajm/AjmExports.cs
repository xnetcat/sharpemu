// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Ajm;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private static readonly ConcurrentDictionary<uint, byte> Contexts = new();
    private static int _nextContextId;

    [SysAbiExport(
        Nid = "dl+4eHSzUu4",
        ExportName = "sceAjmInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInitialize(CpuContext ctx)
    {
        // The ABI argument is a 32-bit reserved value. Upper register bits are
        // unspecified; Silent Hill leaves 0x00000003 in RDI's upper half while
        // passing the required zero value in the low 32 bits.
        var reserved = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (reserved != 0 || outputAddress == 0)
        {
            return SetReturn(ctx, OrbisAjmErrorInvalidParameter);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return SetReturn(ctx, OrbisAjmErrorInvalidParameter);
        }

        Contexts[contextId] = 0;
        Trace($"initialize raw_reserved=0x{ctx[CpuRegister.Rdi]:X16} out=0x{outputAddress:X16} context={contextId}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        Contexts.TryRemove(contextId, out _);
        Trace($"finalize context={contextId}");
        return SetReturn(ctx, 0);
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
        var reserved = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (reserved != 0 || !Contexts.ContainsKey(contextId))
        {
            return SetReturn(ctx, OrbisAjmErrorInvalidParameter);
        }

        Trace($"module_register context={contextId} codec={codecType} reserved={reserved}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        Trace($"module_unregister context={unchecked((uint)ctx[CpuRegister.Rdi])} codec={unchecked((uint)ctx[CpuRegister.Rsi])}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // The batch buffer belongs to the guest; hardware only resets its
        // submission cursor here and does not return an additional object.
        Trace($"batch_initialize context={unchecked((uint)ctx[CpuRegister.Rdi])} batch=0x{ctx[CpuRegister.Rsi]:X16}");
        return SetReturn(ctx, 0);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }
}
