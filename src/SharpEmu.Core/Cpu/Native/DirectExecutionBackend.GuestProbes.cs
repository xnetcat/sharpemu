// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.Core.Loader;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    private static readonly ulong? ProbeRcxFilter =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_PROBE_RCX"), System.Globalization.NumberStyles.HexNumber, null, out var rcx) ? rcx : null;

    private static readonly long ProbeLogLimit =
        long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_PROBE_LIMIT"), out var limit) ? limit : 16;

    // Logs a probed guest call, then runs the 'push rbp; mov rbp, rsp' the probe replaced.
    private unsafe bool TryRunGuestProbe(void* contextRecord, ulong rip)
    {
        if (GuestProbes.Installed.IsEmpty || !GuestProbes.Installed.TryGetValue(rip, out var probe))
        {
            return false;
        }

        var hits = Interlocked.Increment(ref probe.Hits);
        var filtered = ProbeRcxFilter is { } rcxFilter && ReadCtxU64(contextRecord, CTX_RCX) != rcxFilter;
        if (!filtered && (hits <= ProbeLogLimit || (hits & (hits - 1)) == 0))
        {
            var rdi = ReadCtxU64(contextRecord, CTX_RDI);
            var rsi = ReadCtxU64(contextRecord, CTX_RSI);
            var returnAddress = *(ulong*)ReadCtxU64(contextRecord, CTX_RSP);
            var detail = string.Empty;
            if (probe.Register is { } register)
            {
                var baseValue = register switch
                {
                    "rdi" => rdi,
                    "rsi" => rsi,
                    "rdx" => ReadCtxU64(contextRecord, CTX_RDX),
                    "rcx" => ReadCtxU64(contextRecord, CTX_RCX),
                    "r8" => ReadCtxU64(contextRecord, CTX_R8),
                    "r9" => ReadCtxU64(contextRecord, CTX_R9),
                    _ => 0ul,
                };
                var buffer = new byte[32];
                detail = baseValue != 0 && TryReadHostBytes(baseValue + probe.Offset, buffer)
                    ? $" [{register}+0x{probe.Offset:X}]=0x{BitConverter.ToUInt64(buffer, 0):X} 0x{BitConverter.ToUInt64(buffer, 8):X} 0x{BitConverter.ToUInt64(buffer, 16):X} 0x{BitConverter.ToUInt64(buffer, 24):X}"
                    : $" [{register}+0x{probe.Offset:X}]=unreadable";
            }

            Console.Error.WriteLine(
                $"[LOADER][PROBE] 0x{rip:X} hit={hits} ret=0x{returnAddress:X} rdi=0x{rdi:X} rsi=0x{rsi:X} " +
                $"rdx=0x{ReadCtxU64(contextRecord, CTX_RDX):X} rcx=0x{ReadCtxU64(contextRecord, CTX_RCX):X} " +
                $"guest=0x{SharpEmu.HLE.GuestThreadExecution.CurrentGuestThreadHandle:X} " +
                $"managed={Environment.CurrentManagedThreadId} rsp=0x{ReadCtxU64(contextRecord, CTX_RSP):X}{detail}");
        }

        var rsp = ReadCtxU64(contextRecord, CTX_RSP) - 8;
        *(ulong*)rsp = ReadCtxU64(contextRecord, CTX_RBP);
        WriteCtxU64(contextRecord, CTX_RSP, rsp);
        WriteCtxU64(contextRecord, CTX_RBP, rsp);
        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)GuestProbes.Prologue.Length);
        return true;
    }
}
