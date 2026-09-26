// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Rendering;

// Records every draw, dispatch, image lookup and DMA operation of one frame, in submission order,
// next to the images the frame dump writes. The frame dump shows that an output is wrong; this log
// shows which pass produced it, which shaders ran, and which work never reached the host.
internal static class FrameCommandLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _sequence;

    // SHARPEMU_LOG_VOLUME_DRAWS=1 prints, for the whole run, every draw that renders into a volume,
    // an array slice range or several instances, with the images and disposition that follow it.
    private static readonly bool VolumeDraws = Environment.GetEnvironmentVariable("SHARPEMU_LOG_VOLUME_DRAWS") == "1";
    private static bool _volumeDraw;
    private static readonly HashSet<(ulong, ulong, ulong, uint)> VolumeSeen = new();

    public static bool Active => _writer is not null || _volumeDraw;

    // Set while a frame is recorded and SHARPEMU_DUMP_TARGET_STEPS_FILE lists color target addresses:
    // snapshots that target after every draw that renders to it, so the draw that corrupts it is found.
    public static Action<ulong, long>? TargetStepDump;

    public static void AfterDraw(RegisterBanks banks)
    {
        var dump = TargetStepDump;
        if (dump is null || _writer is null)
        {
            return;
        }

        long sequence;
        lock (Gate)
        {
            sequence = _sequence - 1;
        }

        var context = banks.Context;
        for (var slot = 0; slot < context.ColorTargets.Length; slot++)
        {
            if ((context.RenderTargetMask >> (slot * 4) & 0xF) != 0)
            {
                dump(context.ColorTargets[slot].BaseAddress, sequence);
            }
        }
    }

    public static void Start(string directory)
    {
        lock (Gate)
        {
            _writer?.Dispose();
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(Path.Combine(directory, "commands.log")) { AutoFlush = false };
            _sequence = 0;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            _writer?.WriteLine($"{_sequence++:D6} {line}");
        }

        if (_volumeDraw)
        {
            Console.Error.WriteLine($"[VOLDRAW] {line}");
        }
    }

    public static void EndOperation() => _volumeDraw = false;

    public static void Draw(string name, uint count, uint instances, RegisterBanks banks, ulong packetAddress)
    {
        var context = banks.Context;
        var shader = banks.Shader;
        var target = context.ColorTargets[0];
        _volumeDraw = VolumeDraws &&
            (target.Depth != 0 || target.SliceMax != 0 || instances > 1 || shader.Vertex.GeometryAddress != 0 || context.ShaderStages != 0x02002000) &&
            VolumeSeen.Add((shader.Vertex.ExportAddress, shader.Pixel.Address, target.BaseAddress, instances));
        if (!Active)
        {
            return;
        }

        Write(
            $"{name} packet=0x{packetAddress:X} count={count} instances={instances} stages=0x{context.ShaderStages:X8} " +
            $"export=0x{shader.Vertex.ExportAddress:X} geometry=0x{shader.Vertex.GeometryAddress:X} pixel=0x{shader.Pixel.Address:X} " +
            $"targetMask=0x{context.RenderTargetMask:X8} shaderMask=0x{context.ShaderInterface.ColorShaderMask:X8} colorMode={context.ColorControl.Mode} " +
            $"cb0=0x{target.BaseAddress:X} {target.Width + 1}x{target.Height + 1}x{target.Depth + 1} slices={target.SliceStart}..{target.SliceMax} " +
            $"db=0x{context.DepthTarget.ZWriteBase:X}");
    }
}
