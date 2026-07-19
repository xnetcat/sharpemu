// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);
    private const int NpWebApi2ErrorInvalidLibraryContextId = unchecked((int)0x80553403);
    private const int NpWebApi2ErrorLibraryContextNotFound = unchecked((int)0x80553404);
    private const int NpWebApi2ErrorLibraryContextMaximum = unchecked((int)0x80553418);
    private const int UserServiceUserIdInvalid = -1;

    private static readonly object StateGate = new();
    private static readonly HashSet<int> LibraryContexts = [];
    private static int _nextLibraryContextId;
    private static int _nextUserContextId;
    private static int _nextPushHandleId;

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        int libraryContextId;
        lock (StateGate)
        {
            if (LibraryContexts.Count >= 0x7FFF)
            {
                return ctx.SetReturn(NpWebApi2ErrorLibraryContextMaximum);
            }

            do
            {
                _nextLibraryContextId++;
                if (_nextLibraryContextId >= 0x8000)
                {
                    _nextLibraryContextId = 1;
                }
            } while (LibraryContexts.Contains(_nextLibraryContextId));

            libraryContextId = _nextLibraryContextId;
            LibraryContexts.Add(libraryContextId);
        }

        TraceNpWebApi2("init", libraryContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var validationError = ValidateLibraryContext(libraryContextId);
        if (validationError != 0)
        {
            return ctx.SetReturn(validationError);
        }

        var handleId = Interlocked.Increment(ref _nextPushHandleId);
        TraceNpWebApi2("create-push-handle", handleId, unchecked((uint)libraryContextId));
        return ctx.SetReturn(handleId);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);
        var validationError = ValidateLibraryContext(libraryContextId);
        if (validationError != 0)
        {
            return ctx.SetReturn(validationError);
        }

        if (userId == UserServiceUserIdInvalid)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        // The offline backend has no network session, but the ABI returns a
        // positive user-context handle. Later request functions can report
        // signed-out/offline status without leaving Unity with a partially
        // initialized NP object.
        var sequence = Interlocked.Increment(ref _nextUserContextId) & 0xFFFF;
        if (sequence == 0)
        {
            sequence = Interlocked.Increment(ref _nextUserContextId) & 0xFFFF;
        }
        var userContextId = (libraryContextId << 16) | sequence;
        TraceNpWebApi2(
            "create-user-context",
            userContextId,
            unchecked((uint)libraryContextId));
        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var validationError = ValidateLibraryContext(libraryContextId);
        if (validationError != 0)
        {
            return ctx.SetReturn(validationError);
        }

        lock (StateGate)
        {
            LibraryContexts.Remove(libraryContextId);
        }
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static int ValidateLibraryContext(int libraryContextId)
    {
        if ((uint)libraryContextId >= 0x8000u)
        {
            return NpWebApi2ErrorInvalidLibraryContextId;
        }

        lock (StateGate)
        {
            return LibraryContexts.Contains(libraryContextId)
                ? 0
                : NpWebApi2ErrorLibraryContextNotFound;
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        int contextCount;
        lock (StateGate)
        {
            contextCount = LibraryContexts.Count;
        }
        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} contexts={contextCount}");
    }
}
