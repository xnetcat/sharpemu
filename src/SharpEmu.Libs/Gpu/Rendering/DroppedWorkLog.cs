// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Rendering;

// Reports each kind of draw or dispatch the executor drops without running, once per reason and
// shader. A dropped pass leaves its outputs stale, which shows up much later as wrong colors or
// missing geometry; this line connects that symptom to the pass that was not executed.
internal static class DroppedWorkLog
{
    private const int MaxLines = 256;
    private static readonly ConcurrentDictionary<(string Reason, ulong Shader), byte> Seen = new();
    private static long _dropped;

    // Every dropped draw and dispatch of the run, including the ones the report deduplicated. A
    // replay asserts on this: a case that drops its work proves nothing about what it renders.
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    public static void Draw(string reason, RegisterBanks banks)
    {
        Interlocked.Increment(ref _dropped);
        if (FrameCommandLog.Active)
        {
            FrameCommandLog.Write($"  dropped: {reason}");
        }

        var shader = banks.Shader;
        if (!TryClaim(reason, shader.Vertex.ExportAddress ^ (shader.Pixel.Address << 1)))
        {
            return;
        }

        var context = banks.Context;
        var target = context.ColorTargets[0];
        Console.Error.WriteLine(
            $"[GPU][WARN] Dropped draw: reason={reason} stages=0x{context.ShaderStages:X8} " +
            $"export=0x{shader.Vertex.ExportAddress:X16} geometry=0x{shader.Vertex.GeometryAddress:X16} pixel=0x{shader.Pixel.Address:X16} " +
            $"target0=0x{target.BaseAddress:X16} {target.Width + 1}x{target.Height + 1}x{target.Depth + 1} layout={target.Layout} " +
            $"slices={target.SliceStart}..{target.SliceMax} targetMask=0x{context.RenderTargetMask:X8} " +
            $"shaderMask=0x{context.ShaderInterface.ColorShaderMask:X8}");
    }

    public static void Dispatch(string reason, ulong shaderAddress, uint groupsX, uint groupsY, uint groupsZ, uint initiator)
    {
        Interlocked.Increment(ref _dropped);
        if (FrameCommandLog.Active)
        {
            FrameCommandLog.Write($"  dropped: {reason}");
        }

        if (!TryClaim(reason, shaderAddress))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[GPU][WARN] Dropped dispatch: reason={reason} shader=0x{shaderAddress:X16} groups={groupsX}x{groupsY}x{groupsZ} initiator=0x{initiator:X8}");
    }

    // Skipped command blocks: a condition read before the GPU wrote it skips work silently.
    public static void ConditionalExecute(ulong address, uint value, uint dwords)
    {
        if (value == 0 && TryClaim("conditional-execute", address))
        {
            Console.Error.WriteLine($"[GPU][WARN] Skipped command block: conditional-execute address=0x{address:X16} value=0 dwords={dwords}");
        }
    }

    public static void Predication(ulong address, ulong value, uint condition, bool skip)
    {
        if (skip && TryClaim("predication", address))
        {
            Console.Error.WriteLine($"[GPU][WARN] Skipped predicated packets: address=0x{address:X16} value=0x{value:X16} condition={condition}");
        }
    }

    // A branch that skips its then-buffer drops every pass inside it; the compare value is read
    // when the packet is parsed, so a flag the GPU writes later is seen as its old value.
    public static void Branch(
        ulong packet, ulong compareAddress, ulong value, ulong reference, ulong mask, uint function, uint mode,
        bool takeThen, uint thenDwords, uint elseDwords)
    {
        var line = $"branch packet=0x{packet:X} compare=0x{compareAddress:X} value=0x{value:X} reference=0x{reference:X} " +
            $"mask=0x{mask:X} function={function} mode={mode} took={(takeThen ? "then" : mode == 2 ? "else" : "none")} " +
            $"then={thenDwords} else={elseDwords}";
        if (FrameCommandLog.Active)
        {
            FrameCommandLog.Write(line);
        }

        if (!takeThen && TryClaim("branch", packet))
        {
            Console.Error.WriteLine($"[GPU][WARN] Skipped command block: {line}");
        }
    }

    private static bool TryClaim(string reason, ulong shader) =>
        Seen.Count < MaxLines && Seen.TryAdd((reason, shader), 0);
}
