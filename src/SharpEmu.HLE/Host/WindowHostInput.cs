// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Routes emulated input through the active cross-platform host window.
/// </summary>
internal sealed class WindowHostInput : IHostInput
{
    public void EnsureStarted()
    {
        // SDL owns device discovery and pumps it on the window thread.
    }

    public int GetGamepadStates(Span<HostGamepadState> destination) =>
        HostWindowInputSource.Current?.GetGamepadStates(destination) ?? 0;

    public string? DescribeConnectedGamepad() =>
        HostWindowInputSource.Current?.DescribeConnectedGamepad();

    public void SetRumble(byte largeMotor, byte smallMotor) =>
        HostWindowInputSource.Current?.SetRumble(largeMotor, smallMotor);

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) =>
        HostWindowInputSource.Current?.SetTriggerRumble(leftTrigger, rightTrigger);

    public void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger) =>
        HostWindowInputSource.Current?.SetAdaptiveTriggerEffect(leftTrigger, rightTrigger);

    public void SetLightbar(byte red, byte green, byte blue) =>
        HostWindowInputSource.Current?.SetLightbar(red, green, blue);

    public void ResetLightbar() => HostWindowInputSource.Current?.ResetLightbar();

    public bool IsHostWindowFocused() =>
        (HostWindowInputSource.Current?.HasKeyboardFocus ?? false) || TestInputFile.HeldKeys().Count != 0;

    private static int _testKeyLogged;

    public bool IsKeyDown(int virtualKey)
    {
        if (TestInputFile.HeldKeys().Contains(virtualKey))
        {
            if (Interlocked.Increment(ref _testKeyLogged) <= 20)
            {
                Console.Error.WriteLine($"[TESTINPUT] key 0x{virtualKey:X2} reported held");
            }

            return true;
        }

        return HostWindowInputSource.Current?.IsKeyDown(virtualKey) ?? false;
    }
}


// Local test aid: SHARPEMU_INPUT_FILE names a file whose contents are the keys held right now
// (names such as enter, esc, up, down, left, right, tab, or letters), so a script can drive the
// game without window focus.
internal static class TestInputFile
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SHARPEMU_INPUT_FILE");
    private static HashSet<int> _held = new();
    private static long _nextRead;

    public static HashSet<int> HeldKeys()
    {
        if (Path is null)
        {
            return _held;
        }

        var now = Environment.TickCount64;
        if (now < _nextRead)
        {
            return _held;
        }

        _nextRead = now + 30;
        var held = new HashSet<int>();
        try
        {
            foreach (var name in File.ReadAllText(Path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = name.ToLowerInvariant() switch
                {
                    "enter" => 0x0D, "esc" => 0x1B, "tab" => 0x09, "backspace" => 0x08, "space" => 0x20,
                    "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
                    { Length: 1 } single when char.IsLetterOrDigit(single[0]) => char.ToUpperInvariant(single[0]),
                    _ => -1,
                };
                if (key >= 0)
                {
                    held.Add(key);
                }
            }
        }
        catch (IOException)
        {
        }

        _held = held;
        return held;
    }
}
