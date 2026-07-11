// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

public static class MsgDialogExports
{
    private const int AlreadyInitialized = unchecked((int)0x80B80002);
    private const int NotInitialized = unchecked((int)0x80B80003);
    private const int ArgNull = unchecked((int)0x80B8000D);

    // Common-dialog status enumeration shared across the dialog libraries.
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    // SceMsgDialogButtonId: default confirmation button.
    private const int ButtonIdOk = 1;

    private static int _initialized;
    private static int _status;
    private static int _lastMode;

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        var result = Interlocked.Exchange(ref _initialized, 1) == 0 ? 0 : AlreadyInitialized;
        if (result == 0)
        {
            Volatile.Write(ref _status, StatusInitialized);
        }

        return SetReturn(ctx, result);
    }

    [SysAbiExport(
        Nid = "b06Hh0DPEaE",
        ExportName = "sceMsgDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, ArgNull);
        }

        if (Volatile.Read(ref _initialized) == 0)
        {
            return SetReturn(ctx, NotInitialized);
        }

        Span<byte> mode = stackalloc byte[sizeof(int)];
        _lastMode = ctx.Memory.TryRead(paramAddress, mode)
            ? BinaryPrimitives.ReadInt32LittleEndian(mode)
            : 0;

        // No host message dialog is presented; complete immediately with the OK
        // button so guest polling advances instead of blocking on user input.
        Volatile.Write(ref _status, StatusFinished);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "CWVW78Qc3fI",
        ExportName = "sceMsgDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "6fIC3XKt2k0",
        ExportName = "sceMsgDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogUpdateStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "Lr8ovHH9l6A",
        ExportName = "sceMsgDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return SetReturn(ctx, ArgNull);
        }

        // SceMsgDialogResult { int mode; int result; int buttonId; ... }.
        Span<byte> result = stackalloc byte[0x20];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result[0x00..], _lastMode);
        BinaryPrimitives.WriteInt32LittleEndian(result[0x04..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(result[0x08..], ButtonIdOk);
        return ctx.Memory.TryWrite(resultAddress, result)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, ArgNull);
    }

    [SysAbiExport(
        Nid = "HTrcDKlFKuM",
        ExportName = "sceMsgDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogClose(CpuContext ctx)
    {
        Volatile.Write(ref _status, StatusFinished);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ePw-kqZmelo",
        ExportName = "sceMsgDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogTerminate(CpuContext ctx)
    {
        Volatile.Write(ref _status, StatusNone);
        Interlocked.Exchange(ref _initialized, 0);
        return SetReturn(ctx, 0);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
