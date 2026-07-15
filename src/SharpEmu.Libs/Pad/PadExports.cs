// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    private const int PrimaryUserId = 1;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;
    private const int TriggerEffectStateSize = 8;

    private static bool _initialized;

    private static readonly bool _tracePad = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PAD"),
        "1",
        StringComparison.Ordinal);
    private static int _padTraceCount;
    private static int _readStateTraceCount;

    private static void TracePad(string message)
    {
        if (_tracePad && Interlocked.Increment(ref _padTraceCount) <= 200)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] pad.{message}");
        }
    }

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var parameterAddress = ctx[CpuRegister.Rcx];
        TracePad($"open user=0x{userId:X} type={type} index={index} param=0x{parameterAddress:X}");
        if (!_initialized)
        {
            return SetReturn(ctx, OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return SetReturn(ctx, OrbisPadErrorDeviceNoHandle);
        }

        // pParam is a valid optional argument (touchpad resolution etc.);
        // rejecting non-null params made UE titles fall back to handle 0 and
        // poll scePadReadState(0) forever on their input screens.
        if (userId != PrimaryUserId || type != StandardPortType || index != 0)
        {
            return SetReturn(ctx, OrbisPadErrorDeviceNotConnected);
        }

        Console.Error.WriteLine("[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options");
        return SetReturn(ctx, PrimaryPadHandle);
    }

    [SysAbiExport(
        Nid = "u1GRHp+oWoY",
        ExportName = "scePadGetHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetHandle(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        TracePad($"get_handle user=0x{userId:X} type={type} index={index}");
        if (!_initialized)
        {
            return SetReturn(ctx, OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return SetReturn(ctx, OrbisPadErrorDeviceNoHandle);
        }

        return userId == PrimaryUserId && type == StandardPortType && index == 0
            ? SetReturn(ctx, PrimaryPadHandle)
            : SetReturn(ctx, OrbisPadErrorDeviceNoHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "gjP9-KQzoUk",
        ExportName = "scePadGetControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return SetReturn(ctx, OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[ControllerInformationSize];
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;
        information[0x0C] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (_tracePad)
        {
            var count = Interlocked.Increment(ref _readStateTraceCount);
            if (count <= 50 || count % 1000 == 0)
            {
                var elapsed = (Stopwatch.GetTimestamp() - PadStartTimestamp) /
                    (double)Stopwatch.Frequency;
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] pad.read_state#{count} handle={handle} t={elapsed:F1}s");
            }
        }
        if (handle != PrimaryPadHandle)
        {
            return SetReturn(ctx, OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "q1cHNfGycLI",
        ExportName = "scePadRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (handle != PrimaryPadHandle)
        {
            return SetReturn(ctx, OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? SetReturn(ctx, 1)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "znaWI0gpuo8",
        ExportName = "scePadGetTriggerEffectState",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetTriggerEffectState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return SetReturn(ctx, OrbisPadErrorInvalidHandle);
        }

        if (stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> state = stackalloc byte[TriggerEffectStateSize];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
    Nid = "W2G-yoyMF5U",
    ExportName = "scePadSetVibrationMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libScePad")]
    public static int PadSetVibrationMode(CpuContext ctx)
    {
        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();

        // A physical gamepad, when present, takes precedence over the keyboard
        // emulation so a real controller drives the guest directly.
        if (!OperatingSystem.IsWindows() && HostWindowInput.GamepadConnected)
        {
            var pad = HostWindowInput.GetGamepadState();
            var padButtons = pad.Buttons;
            if (IsAutoCrossActive())
            {
                padButtons |= 0x4000;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], padButtons);
            data[0x04] = pad.LeftX;
            data[0x05] = pad.LeftY;
            data[0x06] = pad.RightX;
            data[0x07] = pad.RightY;
            data[0x08] = pad.L2;
            data[0x09] = pad.R2;
            BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
            data[0x4C] = 1;
            var padTicks = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteUInt64LittleEndian(
                data[0x50..],
                ((ulong)(padTicks / Stopwatch.Frequency) * 1_000_000UL) +
                ((ulong)(padTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency));
            data[0x68] = 1;
            return ctx.Memory.TryWrite(dataAddress, data);
        }

        var acceptsKeyboardInput = IsEmulatorWindowFocused();
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons() : 0;
        if (IsAutoCrossActive())
        {
            buttons |= 0x4000;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        UpdateAnalogRamps(acceptsKeyboardInput);
        var leftX = acceptsKeyboardInput ? RampToByte(0) : (byte)128;
        var leftY = acceptsKeyboardInput ? RampToByte(1) : (byte)128;
        var rightX = acceptsKeyboardInput ? RampToByte(2) : (byte)128;
        var rightY = acceptsKeyboardInput ? RampToByte(3) : (byte)128;
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = acceptsKeyboardInput && IsKeyDown(0x52) ? (byte)255 : (byte)0;
        data[0x09] = acceptsKeyboardInput && IsKeyDown(0x46) ? (byte)255 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        var timestampTicks = Stopwatch.GetTimestamp();
        var timestampMicroseconds =
            ((ulong)(timestampTicks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(timestampTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);
        data[0x68] = 1;

        return ctx.Memory.TryWrite(dataAddress, data);
    }

    private static readonly long PadStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double[] AutoCrossTimes = ParseAutoCrossTimes();
    private static readonly double AutoCrossHoldSeconds = ParseAutoCrossHoldSeconds();
    private static readonly bool AutoCrossCatchUp = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AUTO_CROSS_CATCH_UP"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceAutoCross = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUTO_CROSS"),
        "1",
        StringComparison.Ordinal);
    private static readonly object AutoCrossGate = new();
    private static long _autoCrossLoggedMask;
    private static int _autoCrossNextIndex;
    private static double _autoCrossActiveUntil;
    private static double _autoCrossReleaseUntil;

    private static double[] ParseAutoCrossTimes()
    {
        // SHARPEMU_AUTO_CROSS="40,52,64": presses Cross for 0.4s at each
        // second offset from process start. Debug aid for unattended runs.
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_AUTO_CROSS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var values = new List<double>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool IsAutoCrossActive()
    {
        var times = AutoCrossTimes;
        if (times.Length == 0)
        {
            return false;
        }

        var elapsed = (Stopwatch.GetTimestamp() - PadStartTimestamp) / (double)Stopwatch.Frequency;
        if (AutoCrossCatchUp)
        {
            lock (AutoCrossGate)
            {
                if (elapsed < _autoCrossActiveUntil)
                {
                    return true;
                }

                if (elapsed < _autoCrossReleaseUntil ||
                    _autoCrossNextIndex >= times.Length ||
                    elapsed < times[_autoCrossNextIndex])
                {
                    return false;
                }

                var index = _autoCrossNextIndex++;
                _autoCrossActiveUntil = elapsed + AutoCrossHoldSeconds;
                _autoCrossReleaseUntil = _autoCrossActiveUntil + 0.5;
                LogAutoCross(index, times[index], elapsed, catchUp: true);
                return true;
            }
        }

        for (var index = 0; index < times.Length; index++)
        {
            var time = times[index];
            if (elapsed >= time && elapsed < time + AutoCrossHoldSeconds)
            {
                LogAutoCross(index, time, elapsed, catchUp: false);
                return true;
            }
        }

        return false;
    }

    private static void LogAutoCross(int index, double scheduled, double elapsed, bool catchUp)
    {
        if (!TraceAutoCross || index >= 64)
        {
            return;
        }

        var bit = 1L << index;
        var previous = Interlocked.Or(ref _autoCrossLoggedMask, bit);
        if ((previous & bit) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][DIAG] pad.auto_cross_consumed index={index} " +
                $"scheduled={scheduled:F3}s elapsed={elapsed:F3}s catch_up={catchUp}");
        }
    }

    private static double ParseAutoCrossHoldSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_AUTO_CROSS_HOLD_MS");
        return double.TryParse(
                   raw,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var milliseconds) &&
               milliseconds is >= 1 and <= 10_000
            ? milliseconds / 1000.0
            : 0.4;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsKeyDown(int vk)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TryMapVirtualKey(vk, out var key) && HostWindowInput.IsKeyDown(key);
        }

        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private static bool TryMapVirtualKey(int vk, out Silk.NET.Input.Key key)
    {
        key = vk switch
        {
            0x08 => Silk.NET.Input.Key.Backspace,
            0x09 => Silk.NET.Input.Key.Tab,
            0x0D => Silk.NET.Input.Key.Enter,
            0x1B => Silk.NET.Input.Key.Escape,
            0x25 => Silk.NET.Input.Key.Left,
            0x26 => Silk.NET.Input.Key.Up,
            0x27 => Silk.NET.Input.Key.Right,
            0x28 => Silk.NET.Input.Key.Down,
            >= 0x41 and <= 0x5A => Silk.NET.Input.Key.A + (vk - 0x41),
            _ => Silk.NET.Input.Key.Unknown,
        };
        return key != Silk.NET.Input.Key.Unknown;
    }

    private static bool IsEmulatorWindowFocused()
    {
        if (!OperatingSystem.IsWindows())
        {
            // user32 is Windows-only; accept keyboard input whenever the
            // presenter window's keyboard is attached (GLFW only delivers
            // key events to the focused window anyway).
            return HostWindowInput.IsConnected;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static uint ReadKeyboardButtons()
    {
        uint buttons = 0;
        // D-pad
        if (IsKeyDown(0x25)) buttons |= 0x0080; // Left
        if (IsKeyDown(0x27)) buttons |= 0x0020; // Right
        if (IsKeyDown(0x26)) buttons |= 0x0010; // Up
        if (IsKeyDown(0x28)) buttons |= 0x0040; // Down
        // Face buttons
        if (IsKeyDown(0x5A) || IsKeyDown(0x0D)) buttons |= 0x4000; // Z / Enter = Cross
        if (IsKeyDown(0x58) || IsKeyDown(0x1B)) buttons |= 0x2000; // X / Escape = Circle
        if (IsKeyDown(0x43)) buttons |= 0x8000; // C = Square
        if (IsKeyDown(0x56)) buttons |= 0x1000; // V = Triangle
        // Shoulder buttons
        if (IsKeyDown(0x51)) buttons |= 0x0400; // Q = L1
        if (IsKeyDown(0x45)) buttons |= 0x0800; // E = R1
        if (IsKeyDown(0x52)) buttons |= 0x0100; // R = L2 (digital)
        if (IsKeyDown(0x46)) buttons |= 0x0200; // F = R2 (digital)
        // Options (Start)
        if (IsKeyDown(0x09) || IsKeyDown(0x08)) buttons |= 0x0008; // Tab / Backspace = Options
        return buttons;
    }

    // Per-axis ramp state in [-1, 1]: keyboard axes accelerate toward full
    // deflection and decay back to center over a short window so movement and
    // aiming feel analog rather than snapping between three states. Order:
    // LX, LY, RX, RY.
    private static readonly float[] _analogRamp = new float[4];
    private static long _lastAnalogRampTimestamp;

    private static readonly (int Negative, int Positive)[] _analogAxisKeys =
    {
        (0x41, 0x44), // A / D  -> left X
        (0x57, 0x53), // W / S  -> left Y
        (0x4A, 0x4C), // J / L  -> right X
        (0x49, 0x4B), // I / K  -> right Y
    };

    private static void UpdateAnalogRamps(bool acceptsKeyboardInput)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var previous = _lastAnalogRampTimestamp;
        _lastAnalogRampTimestamp = now;
        if (!acceptsKeyboardInput || previous == 0)
        {
            return;
        }

        var dt = (float)((now - previous) / (double)System.Diagnostics.Stopwatch.Frequency);
        // Reach full deflection in ~140 ms and recentre in ~90 ms.
        const float attackPerSecond = 7.0f;
        const float releasePerSecond = 11.0f;
        for (var axis = 0; axis < _analogRamp.Length; axis++)
        {
            var negative = IsKeyDown(_analogAxisKeys[axis].Negative);
            var positive = IsKeyDown(_analogAxisKeys[axis].Positive);
            var target = positive && !negative ? 1f : negative && !positive ? -1f : 0f;
            var current = _analogRamp[axis];
            var rate = (target == 0f ? releasePerSecond : attackPerSecond) * dt;
            if (current < target)
            {
                current = Math.Min(target, current + rate);
            }
            else if (current > target)
            {
                current = Math.Max(target, current - rate);
            }

            _analogRamp[axis] = current;
        }
    }

    private static byte RampToByte(int axis) =>
        (byte)Math.Clamp((int)MathF.Round(128f + _analogRamp[axis] * 127f), 0, 255);
}
