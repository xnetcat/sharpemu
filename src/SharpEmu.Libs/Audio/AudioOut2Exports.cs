// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // FMOD's PS5 backend allocates this ABI structure as four 16-byte lanes.
    // Clearing 0x80 bytes here overwrote the caller's stack canary immediately
    // following the 0x40-byte parameter block.
    private const int AudioOut2ContextParamSize = 0x40;
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const int AudioOut2ContextMemoryAlignment = 0x10000;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;

    // Per-context audio parameters captured at ContextCreate so ContextAdvance
    // can pace to the real playback cadence (grain samples at the sample rate).
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();

    // Modeled hardware output-queue depth: a submitted grain plus one in
    // flight. Wwise primes the sink until the queue level reported by
    // sceAudioOut2ContextGetQueueLevel actually rises, so the level must be
    // derived from a live model rather than hardcoded to zero.
    private const uint ContextQueueDepth = 2;

    // Blocking submission is opt-in. The guest calls Advance/Push from inside
    // its own critical sections, so any wait here is held across a guest lock:
    // measured on Silent Hill, a per-grain wait kept Wwise's bank-manager mutex
    // busy continuously and cut AK::BankManager from 153k to 19k imports per
    // run. Backpressure is therefore reported through the queue level, which a
    // producer polls, rather than enforced by sleeping in the call.
    private static readonly bool _blockingSubmitEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AUDIOOUT2_PACE"),
        "1",
        StringComparison.Ordinal);

    /// <summary>Live mastering chains, keyed by the handle handed back to the title.</summary>
    private static readonly ConcurrentDictionary<ulong, ulong> MasteringHandles = new();

    private const ulong MasteringHandleTag = 0x2001_0000_0000UL;

    private sealed class ContextState
    {
        private readonly object _paceGate = new();
        private long _queueTailTimestamp;

        public ContextState(uint frequency, uint channels, uint grainSamples)
        {
            Frequency = frequency == 0 ? 48000 : frequency;
            Channels = channels == 0 ? 2 : channels;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
        }

        public uint Frequency { get; }
        public uint Channels { get; }
        public uint GrainSamples { get; }

        private long GrainTicks() =>
            checked((long)Math.Ceiling(Stopwatch.Frequency * (double)GrainSamples / Frequency));


        public (uint Queued, uint Available) ReadQueueLevel()
        {
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                var remaining = _queueTailTimestamp - now;
                if (remaining <= 0)
                {
                    return (0u, ContextQueueDepth);
                }

                var grainTicks = GrainTicks();
                var queued = (uint)Math.Min(
                    ContextQueueDepth,
                    (ulong)((remaining + grainTicks - 1) / grainTicks));
                return (queued, ContextQueueDepth - queued);
            }
        }

        // Records a grain entering the modeled output queue. The queue drains
        // by wall-clock grain time, so ReadQueueLevel reports real occupancy
        // and a polling producer paces itself against it. Waiting here is
        // opt-in only: the guest calls this while holding its own locks.
        public void SubmitGrain()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                var grainTicks = GrainTicks();
                if (_queueTailTimestamp < now)
                {
                    _queueTailTimestamp = now;
                }

                var slack = _queueTailTimestamp - now - grainTicks * (ContextQueueDepth - 1);
                delay = slack > 0 ? slack : 0;
                _queueTailTimestamp += grainTicks;
            }

            if (delay > 0 && _blockingSubmitEnabled)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], AudioOut2ContextParamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 0x400);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memoryInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The out structure is a single 64-bit memory size. Silent Hill's Wwise
        // sink allocates exactly eight bytes for it, immediately below the
        // context-param block it passes to ContextCreate right after — writing
        // the former 0x20-byte guess corrupted that param block in place.
        return TryWriteUInt64(ctx, memoryInfoAddress, AudioOut2ContextMemorySize)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Read channels/frequency/grain from the reset-param blob so the
        // context can pace advances to the real audio cadence.
        uint channels = 2;
        uint frequency = 48000;
        uint grain = 256;
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (ctx.Memory.TryRead(paramAddress, param))
        {
            var pc = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
            var pf = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
            var pg = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
            if (pc is > 0 and <= 8) channels = pc;
            if (pf is >= 8000 and <= 192000) frequency = pf;
            // Values below one cache line are flags/counts in observed PS5
            // callers, not audio grains. Keep the hardware-sized default.
            if (pg is >= 64 and <= 0x4000) grain = pg;
            TraceAudioOut2($"context-param address=0x{paramAddress:X} bytes={Convert.ToHexString(param)}");
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Contexts[handle] = new ContextState(frequency, channels, grain);
        TraceAudioOut2($"context-create handle=0x{handle:X} frequency={frequency} channels={channels} grain={grain} memory=0x{memoryAddress:X} size=0x{memorySize:X}");
        return TryWriteUInt64(ctx, outContextAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        Contexts.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "DxGyV8dtOR8",
        ExportName = "sceAudioOut2ContextBedWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextBedWrite(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var traceCount = Interlocked.Increment(ref _pushTraceCount);
        if (traceCount <= 16)
        {
            TraceAudioOut2($"context-push count={traceCount} rdi=0x{handle:X} rsi=0x{ctx[CpuRegister.Rsi]:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{ctx[CpuRegister.Rcx]:X}");
        }

        if (Contexts.TryGetValue(handle, out var context))
        {
            // FMOD's PS5 output path uses ContextPush as the submission clock
            // and does not call ContextAdvance. Pace pushes to one hardware
            // grain so the feeder cannot outrun playback and starve the game.
            context.SubmitGrain();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        // Advancing renders one grain of audio on hardware; pace it to the same
        // wall-clock cadence so the guest audio thread runs at the right speed.
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var context))
        {
            context.SubmitGrain();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        // The ABI exposes two adjacent 32-bit levels (queued, available). Writing
        // one 64-bit value corrupts the next stack local when the outputs are
        // four bytes apart — Silent Hill's Wwise sink keeps its stack canary
        // right behind them and dies in __stack_chk_fail. The values come from
        // the modeled queue: Wwise's sink-priming loop pushes grains until the
        // reported level rises, holding its bank-manager mutex the whole time,
        // so a hardcoded zero deadlocks the entire audio init.
        uint queuedGrains = 0;
        var availableGrains = ContextQueueDepth;
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var context))
        {
            (queuedGrains, availableGrains) = context.ReadQueueLevel();
        }

        var queuedAddress = ctx[CpuRegister.Rsi];
        var availableAddress = ctx[CpuRegister.Rdx];
        if ((queuedAddress != 0 && !TryWriteUInt32(ctx, queuedAddress, queuedGrains)) ||
            (availableAddress != 0 && !TryWriteUInt32(ctx, availableAddress, availableGrains)))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var type = unchecked((int)ctx[CpuRegister.Rdi]);
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        var contextAddress = ctx[CpuRegister.Rcx];
        if (type < 0 || type > 255 || paramAddress == 0 || outPortAddress == 0 || contextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)(uint)type << 16) | portId;
        return TryWriteUInt64(ctx, outPortAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var type = (int)((handle >> 16) & 0xFF);
        Span<byte> state = stackalloc byte[0x20];
        state.Clear();
        var output = type == 2 ? 0x40 : 0x01;
        var channels = type == 2 ? 1 : 2;
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[0x40];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 48000);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 0x10000000 && userId != 255) ||
            outUserAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return TryWriteUInt64(ctx, outUserAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    /// <summary>
    /// Creates the mastering (output-bus limiter/EQ) chain for a context. SharpEmu applies no
    /// mastering, so the call succeeds and publishes a handle the title can pass back to
    /// SetParam/Term without the chain doing anything to the samples.
    /// </summary>
    [SysAbiExport(
        Nid = "XHl38ZNknbs",
        ExportName = "sceAudioOut2MasteringInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringInit(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The handle is derived from the context so Term can reject a mismatched pairing
        // instead of accepting any value.
        var handle = MasteringHandleTag | (contextHandle & 0xFFFF);
        MasteringHandles[handle] = contextHandle;
        TraceAudioOut2(
            $"mastering-init context=0x{contextHandle:X} param=0x{paramAddress:X} handle=0x{handle:X}");

        return outHandleAddress == 0 || TryWriteUInt64(ctx, outHandleAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    /// <summary>
    /// Updates one mastering parameter. Accepted and recorded in the trace, but not applied:
    /// SharpEmu does not model the output-bus processor.
    /// </summary>
    [SysAbiExport(
        Nid = "v8iOE+j8a5o",
        ExportName = "sceAudioOut2MasteringSetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringSetParam(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || !MasteringHandles.ContainsKey(handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAudioOut2($"mastering-set-param handle=0x{handle:X} param=0x{paramAddress:X}");
        return SetReturn(ctx, 0);
    }

    /// <summary>Tears down a mastering chain created by <c>sceAudioOut2MasteringInit</c>.</summary>
    [SysAbiExport(
        Nid = "2bbBBOkH4CY",
        ExportName = "sceAudioOut2MasteringTerm",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringTerm(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0 || !MasteringHandles.TryRemove(handle, out _))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAudioOut2($"mastering-term handle=0x{handle:X}");
        return SetReturn(ctx, 0);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceAudioOut2(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_OUT2"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audio_out2.{message}");
        }
    }
}
