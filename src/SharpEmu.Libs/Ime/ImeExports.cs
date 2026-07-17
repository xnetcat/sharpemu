// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    private const int ImeErrorNotOpened = unchecked((int)0x80BC0005);
    private const int ImeErrorConnectionFailed = unchecked((int)0x80BC0007);
    private static readonly bool SimulateNoKeyboard = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_IME_NO_KEYBOARD"),
        "1",
        StringComparison.Ordinal);

    // Quake (KEX) calls this from its main loop and from the audio bring-up path with
    // an event-handler pointer. No IME session ever exists here, so report success
    // without invoking the handler ("no pending IME events"). This NID was previously
    // misbound as an sceNgs2VoiceControl alias, which fed the game NGS2 errors.
    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        var result = SimulateNoKeyboard ? ImeErrorNotOpened : 0;
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        var result = SimulateNoKeyboard ? ImeErrorConnectionFailed : 0;
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
