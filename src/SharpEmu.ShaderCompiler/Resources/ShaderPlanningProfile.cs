// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.ShaderCompiler.Resources;

// LOCAL ONLY: first-use planning attribution. Nested scopes are inclusive;
// allocations count this thread only and elapsed time includes scheduling/GC.
internal readonly struct ShaderPlanningProfile : IDisposable
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SHADER_PLANNING") == "1";
    private readonly long _start;
    private readonly long _allocated;
    private readonly ulong _hash;
    private readonly string _phase;
    private readonly ShaderStage _stage;

    internal ShaderPlanningProfile(ShaderStage stage, ulong hash, string phase)
    {
        _stage = stage;
        _hash = hash;
        _phase = phase;
        _allocated = Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _start = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    public void Dispose()
    {
        if (!Enabled) return;
        var ticks = Stopwatch.GetTimestamp() - _start;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - _allocated;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][SHADER_PLAN] stage={_stage} hash=0x{_hash:X16} phase={_phase} elapsed_ms={ticks * 1000.0 / Stopwatch.Frequency:F3} allocated_bytes={bytes}"));
    }
}
