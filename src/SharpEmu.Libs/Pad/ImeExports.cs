// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Pad;

public static class ImeExports
{
    private const int PrimaryUserId = 1;
    private const int LegacyPrimaryUserId = 0x10000000;
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0001);
    private const int ImeErrorInvalidUserId = unchecked((int)0x80BC0010);
    private const int ImeErrorNotOpened = unchecked((int)0x80BC0005);
    private const int ImeErrorConnectionFailed = unchecked((int)0x80BC0007);

    private static bool _keyboardOpen;

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, ImeErrorInvalidAddress);
        }

        if (userId is not PrimaryUserId and not LegacyPrimaryUserId)
        {
            return SetReturn(ctx, ImeErrorInvalidUserId);
        }

        // No physical USB keyboard is connected on a stock console, and no
        // host keyboard-event bridge exists here. Reporting success makes
        // titles (Silent Hill TSM's pre-title screen) wait forever for key
        // events that never arrive; the authentic no-keyboard behavior is a
        // connection failure, after which titles fall back to pad input.
        return SetReturn(ctx, ImeErrorConnectionFailed);
    }

    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx) =>
        SetReturn(ctx, _keyboardOpen ? 0 : ImeErrorNotOpened);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
