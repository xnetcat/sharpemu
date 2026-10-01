// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5PixelInputMapping
{
    public static uint[] ResolveLocations(
        ReadOnlySpan<uint> controls,
        ReadOnlySpan<uint> activeInputs)
    {
        var locations = new uint[activeInputs.Length];
        ResolveLocations(controls, activeInputs, locations);
        return locations;
    }

    public static void ResolveLocations(
        ReadOnlySpan<uint> controls,
        ReadOnlySpan<uint> activeInputs,
        Span<uint> locations)
    {
        if (locations.Length < activeInputs.Length)
            throw new ArgumentException("The output cannot hold every active input.", nameof(locations));
        Span<bool> usedLocations = stackalloc bool[32];
        usedLocations.Clear();

        for (var index = 0; index < activeInputs.Length; index++)
        {
            var input = activeInputs[index];
            var location = input < (uint)controls.Length
                ? controls[(int)input] & 0x1Fu
                : input;
            if (location < 32u && usedLocations[(int)location])
            {
                location = input;
                while (location < 32u && usedLocations[(int)location])
                {
                    location++;
                }

                if (location >= 32u)
                {
                    throw new InvalidOperationException(
                        "pixel input locations exceed the hardware interface limit");
                }
            }

            locations[index] = location;
            if (location < 32u)
            {
                usedLocations[(int)location] = true;
            }
        }
    }
}
