// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Input;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Keyboard and gamepad state sampled from the presenter's window, used by the
/// pad exports on hosts without user32 (macOS/Linux). The presenter attaches
/// the window's input context once the window exists; input events arrive on
/// the window thread and pad reads happen on guest threads, so the shared state
/// is guarded.
/// </summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<Key> Pressed = new();
    private static volatile bool _connected;

    // Orbis (DualSense) digital button bitmask, matching PadExports' layout.
    private const uint OrbisL3 = 0x0002;
    private const uint OrbisR3 = 0x0004;
    private const uint OrbisOptions = 0x0008;
    private const uint OrbisUp = 0x0010;
    private const uint OrbisRight = 0x0020;
    private const uint OrbisDown = 0x0040;
    private const uint OrbisLeft = 0x0080;
    private const uint OrbisL2 = 0x0100;
    private const uint OrbisR2 = 0x0200;
    private const uint OrbisL1 = 0x0400;
    private const uint OrbisR1 = 0x0800;
    private const uint OrbisTriangle = 0x1000;
    private const uint OrbisCircle = 0x2000;
    private const uint OrbisCross = 0x4000;
    private const uint OrbisSquare = 0x8000;

    // Analog trigger threshold above which L2/R2 register as digital presses.
    private const float TriggerDigitalThreshold = 0.12f;

    private static readonly object PadGate = new();
    private static volatile bool _gamepadConnected;
    private static uint _gamepadButtons;
    private static float _leftX, _leftY, _rightX, _rightY, _leftTrigger, _rightTrigger;

    /// <summary>True once a window keyboard is delivering events.</summary>
    public static bool IsConnected => _connected;

    /// <summary>True once a physical gamepad has been detected.</summary>
    public static bool GamepadConnected => _gamepadConnected;

    public static void Attach(IInputContext input)
    {
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.F1)
                {
                    VideoOut.PerfOverlay.Toggle();
                }

                lock (Gate)
                {
                    Pressed.Add(key);
                }
            };
            keyboard.KeyUp += (_, key, _) =>
            {
                lock (Gate)
                {
                    Pressed.Remove(key);
                }
            };
        }

        if (input.Keyboards.Count > 0)
        {
            _connected = true;
        }

        foreach (var gamepad in input.Gamepads)
        {
            AttachGamepad(gamepad);
        }

        // Handle controllers plugged in after the window opens.
        input.ConnectionChanged += (device, connected) =>
        {
            if (device is IGamepad gamepad && connected)
            {
                AttachGamepad(gamepad);
            }
        };
    }

    private static void AttachGamepad(IGamepad gamepad)
    {
        _gamepadConnected = true;
        gamepad.ButtonDown += (_, button) => SetGamepadButton(button.Name, pressed: true);
        gamepad.ButtonUp += (_, button) => SetGamepadButton(button.Name, pressed: false);
        gamepad.ThumbstickMoved += (_, stick) =>
        {
            lock (PadGate)
            {
                if (stick.Index == 0)
                {
                    _leftX = stick.X;
                    _leftY = stick.Y;
                }
                else if (stick.Index == 1)
                {
                    _rightX = stick.X;
                    _rightY = stick.Y;
                }
            }
        };
        gamepad.TriggerMoved += (_, trigger) =>
        {
            var value = trigger.Position;
            lock (PadGate)
            {
                if (trigger.Index == 0)
                {
                    _leftTrigger = value;
                }
                else if (trigger.Index == 1)
                {
                    _rightTrigger = value;
                }
            }

            SetGamepadButton(
                trigger.Index == 0 ? ButtonName.LeftBumper : ButtonName.RightBumper,
                pressed: false,
                triggerBit: trigger.Index == 0 ? OrbisL2 : OrbisR2,
                triggerPressed: value >= TriggerDigitalThreshold);
        };
    }

    private static void SetGamepadButton(
        ButtonName name,
        bool pressed,
        uint triggerBit = 0,
        bool triggerPressed = false)
    {
        if (triggerBit != 0)
        {
            lock (PadGate)
            {
                if (triggerPressed)
                {
                    _gamepadButtons |= triggerBit;
                }
                else
                {
                    _gamepadButtons &= ~triggerBit;
                }
            }

            return;
        }

        var bit = MapButton(name);
        if (bit == 0)
        {
            return;
        }

        lock (PadGate)
        {
            if (pressed)
            {
                _gamepadButtons |= bit;
            }
            else
            {
                _gamepadButtons &= ~bit;
            }
        }
    }

    private static uint MapButton(ButtonName name) => name switch
    {
        ButtonName.A => OrbisCross,
        ButtonName.B => OrbisCircle,
        ButtonName.X => OrbisSquare,
        ButtonName.Y => OrbisTriangle,
        ButtonName.LeftBumper => OrbisL1,
        ButtonName.RightBumper => OrbisR1,
        ButtonName.Start => OrbisOptions,
        ButtonName.LeftStick => OrbisL3,
        ButtonName.RightStick => OrbisR3,
        ButtonName.DPadUp => OrbisUp,
        ButtonName.DPadDown => OrbisDown,
        ButtonName.DPadLeft => OrbisLeft,
        ButtonName.DPadRight => OrbisRight,
        _ => 0,
    };

    public static bool IsKeyDown(Key key)
    {
        lock (Gate)
        {
            return Pressed.Contains(key);
        }
    }

    /// <summary>
    /// Current gamepad state in Orbis form: digital button mask plus stick and
    /// trigger axes normalized to the pad byte range (sticks 0..255 with 128
    /// centered, triggers 0..255). Only meaningful when <see cref="GamepadConnected"/>.
    /// </summary>
    public static (uint Buttons, byte LeftX, byte LeftY, byte RightX, byte RightY, byte L2, byte R2)
        GetGamepadState()
    {
        lock (PadGate)
        {
            return (
                _gamepadButtons,
                AxisToByte(_leftX),
                AxisToByte(-_leftY),
                AxisToByte(_rightX),
                AxisToByte(-_rightY),
                UnitToByte(_leftTrigger),
                UnitToByte(_rightTrigger));
        }
    }

    // Stick axes arrive as -1..1 (Y up-positive); the pad wants 0..255 with 128
    // centered and Y increasing downward, hence the inversion at the call site.
    private static byte AxisToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round((value + 1f) * 0.5f * 255f), 0, 255);

    private static byte UnitToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
