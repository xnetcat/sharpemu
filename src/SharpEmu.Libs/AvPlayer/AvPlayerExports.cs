// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Text;

namespace SharpEmu.Libs.AvPlayer;

public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int MaxGuestPathLength = 1024;

    [SysAbiExport(
        Nid = "aS66RI0gGgo",
        ExportName = "sceAvPlayerInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInit(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        AvPlayerSessions.Create(handle);
        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(
        Nid = "HD1YKVU26-M",
        ExportName = "sceAvPlayerPostInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPostInit(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        return SetReturn(
            ctx,
            handle != 0 && dataAddress != 0 && AvPlayerSessions.TryGet(handle, out _)
                ? 0
                : InvalidParameters);
    }

    [SysAbiExport(
        Nid = "o9eWRkSL+M4",
        ExportName = "sceAvPlayerInitEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInitEx(CpuContext ctx) => AvPlayerInit(ctx);

    [SysAbiExport(
        Nid = "NkJwDzKmIlw",
        ExportName = "sceAvPlayerClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerClose(CpuContext ctx)
    {
        if (AvPlayerSessions.Remove(ctx[CpuRegister.Rdi], out var session))
        {
            session.Dispose();
            return SetReturn(ctx, 0);
        }

        return SetReturn(ctx, InvalidParameters);
    }

    [SysAbiExport(
        Nid = "KMcEa+rHsIo",
        ExportName = "sceAvPlayerAddSource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSource(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        // sceAvPlayerAddSource(handle, const char* filename): the path pointer is
        // in rsi. Probe failures degrade gracefully to "finished immediately".
        if (AvPlayerSessions.PlaybackEnabled &&
            TryReadGuestString(ctx, ctx[CpuRegister.Rsi], out var path))
        {
            session.AddSource(path);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "x8uvuFOPZhU",
        ExportName = "sceAvPlayerAddSourceEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSourceEx(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        // sceAvPlayerAddSourceEx(handle, sourceType, SceAvPlayerSourceDetails*):
        // the URI string pointer lives at details+0x00 (SceAvPlayerUri.name). Fall
        // back to treating rsi/rdx as a direct path pointer for SDK variants that
        // pass (handle, const char*).
        if (AvPlayerSessions.PlaybackEnabled && TryReadSourceExPath(ctx, out var path))
        {
            session.AddSource(path);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ET4Gr-Uu07s",
        ExportName = "sceAvPlayerStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStart(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        if (AvPlayerSessions.PlaybackEnabled)
        {
            session.Start();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ZC17w3vB5Lo",
        ExportName = "sceAvPlayerStop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStop(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        session.Stop();
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "9y5v+fGN4Wk",
        ExportName = "sceAvPlayerPause",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPause(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        session.Pause();
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "w5moABNwnRY",
        ExportName = "sceAvPlayerResume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerResume(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        session.Resume();
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "OVths0xGfho",
        ExportName = "sceAvPlayerSetLooping",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLooping(CpuContext ctx)
    {
        if (!AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        session.SetLooping(ctx[CpuRegister.Rsi] != 0);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ODJK2sn9w4A",
        ExportName = "sceAvPlayerEnableStream",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerEnableStream(CpuContext ctx) => ValidatePlayer(ctx);

    [SysAbiExport(
        Nid = "XC9wM+xULz8",
        ExportName = "sceAvPlayerJumpToTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerJumpToTime(CpuContext ctx) => ValidatePlayer(ctx);

    [SysAbiExport(
        Nid = "yN7Jhuv8g24",
        ExportName = "sceAvPlayerVprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerVprintf(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "UbQoYawOsfY",
        ExportName = "sceAvPlayerIsActive",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerIsActive(CpuContext ctx)
    {
        if (!AvPlayerSessions.PlaybackEnabled ||
            !AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, 0);
        }

        return SetReturn(ctx, session.IsActive() ? 1 : 0);
    }

    [SysAbiExport(
        Nid = "o3+RWnHViSg",
        ExportName = "sceAvPlayerGetVideoData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoData(CpuContext ctx)
    {
        if (!AvPlayerSessions.PlaybackEnabled ||
            !AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, 0);
        }

        return SetReturn(ctx, session.GetVideoData(ctx, ctx[CpuRegister.Rsi], extended: false));
    }

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx)
    {
        if (!AvPlayerSessions.PlaybackEnabled ||
            !AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, 0);
        }

        return SetReturn(ctx, session.GetVideoData(ctx, ctx[CpuRegister.Rsi], extended: true));
    }

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx)
    {
        if (!AvPlayerSessions.PlaybackEnabled ||
            !AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            return SetReturn(ctx, 0);
        }

        return SetReturn(ctx, session.GetAudioData(ctx, ctx[CpuRegister.Rsi]));
    }

    [SysAbiExport(
        Nid = "wwM99gjFf1Y",
        ExportName = "sceAvPlayerCurrentTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerCurrentTime(CpuContext ctx)
    {
        if (!AvPlayerSessions.PlaybackEnabled ||
            !AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out var session))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = session.CurrentTimeMs();
        return 0;
    }

    [SysAbiExport(
        Nid = "hdTyRzCXQeQ",
        ExportName = "sceAvPlayerStreamCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStreamCount(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "d8FcbzfAdQw",
        ExportName = "sceAvPlayerGetStreamInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfo(CpuContext ctx) => SetReturn(ctx, InvalidParameters);

    private static bool TryReadSourceExPath(CpuContext ctx, out string path)
    {
        // Preferred form: rdx points at SceAvPlayerSourceDetails whose first field
        // is SceAvPlayerUri { const char* name; u32 length; }.
        var detailsAddress = ctx[CpuRegister.Rdx];
        if (detailsAddress != 0 &&
            ctx.TryReadUInt64(detailsAddress, out var uriName) &&
            TryReadGuestString(ctx, uriName, out path))
        {
            return true;
        }

        // Fallbacks for SDK revisions that pass a raw path pointer.
        return TryReadGuestString(ctx, ctx[CpuRegister.Rdx], out path) ||
               TryReadGuestString(ctx, ctx[CpuRegister.Rsi], out path);
    }

    private static bool TryReadGuestString(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        Span<byte> chunk = stackalloc byte[64];
        var builder = new StringBuilder();
        var offset = 0UL;
        while (builder.Length < MaxGuestPathLength)
        {
            if (!ctx.Memory.TryRead(address + offset, chunk))
            {
                // Retry byte-by-byte so a string ending near an unmapped page
                // boundary still reads up to its terminator.
                return TryReadGuestStringByteWise(ctx, address, out value);
            }

            var terminator = chunk.IndexOf((byte)0);
            var take = terminator < 0 ? chunk.Length : terminator;
            for (var i = 0; i < take; i++)
            {
                builder.Append((char)chunk[i]);
            }

            if (terminator >= 0)
            {
                value = builder.ToString();
                return value.Length > 0;
            }

            offset += (ulong)chunk.Length;
        }

        value = builder.ToString();
        return value.Length > 0;
    }

    private static bool TryReadGuestStringByteWise(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        var builder = new StringBuilder();
        Span<byte> single = stackalloc byte[1];
        for (var offset = 0UL; builder.Length < MaxGuestPathLength; offset++)
        {
            if (!ctx.Memory.TryRead(address + offset, single))
            {
                break;
            }

            if (single[0] == 0)
            {
                break;
            }

            builder.Append((char)single[0]);
        }

        value = builder.ToString();
        return value.Length > 0;
    }

    private static int ValidatePlayer(CpuContext ctx) =>
        SetReturn(ctx, AvPlayerSessions.TryGet(ctx[CpuRegister.Rdi], out _) ? 0 : InvalidParameters);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
