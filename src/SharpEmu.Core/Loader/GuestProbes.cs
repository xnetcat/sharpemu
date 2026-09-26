// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

// Diagnostic function-entry probes. SHARPEMU_GUEST_PROBE lists guest addresses separated by ',' or ';',
// each optionally followed by ':<register>+<hex offset>' naming one qword to print. The loader
// replaces the standard 'push rbp; mov rbp, rsp' prologue at each address with UD2; the fault
// handler logs the call and runs the replaced prologue, so the guest continues unchanged.
public static class GuestProbes
{
    public static readonly byte[] Prologue = [0x55, 0x48, 0x89, 0xE5];

    public sealed record Probe(ulong Address, string? Register, ulong Offset)
    {
        public long Hits;
    }

    private static readonly Dictionary<ulong, Probe> Requested = Parse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_PROBE"));

    public static readonly ConcurrentDictionary<ulong, Probe> Installed = new();

    public static void Install(PhysicalVirtualMemory memory, ulong imageBase, ulong imageSize)
    {
        Span<byte> original = stackalloc byte[4];
        foreach (var probe in Requested.Values)
        {
            if (probe.Address < imageBase || probe.Address - imageBase >= imageSize || Installed.ContainsKey(probe.Address))
            {
                if (probe.Address >= imageBase && probe.Address - imageBase < imageSize + 0x10000000)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Guest probe 0x{probe.Address:X} skipped: image 0x{imageBase:X}+0x{imageSize:X}.");
                }

                continue;
            }

            if (!memory.TryRead(probe.Address, original) || !original.SequenceEqual(Prologue))
            {
                Console.Error.WriteLine($"[LOADER][WARN] Guest probe 0x{probe.Address:X}: no standard prologue ({Convert.ToHexString(original)}).");
                continue;
            }

            if (memory.TryWrite(probe.Address, [0x0F, 0x0B, 0x90, 0x90]))
            {
                Installed[probe.Address] = probe;
                Console.Error.WriteLine($"[LOADER][INFO] Guest probe installed at 0x{probe.Address:X}.");
            }
        }
    }

    private static Dictionary<ulong, Probe> Parse(string? value)
    {
        var probes = new Dictionary<ulong, Probe>();
        foreach (var entry in (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2);
            if (!ulong.TryParse(parts[0].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                continue;
            }

            string? register = null;
            ulong offset = 0;
            if (parts.Length == 2)
            {
                var memory = parts[1].Split('+', 2);
                register = memory[0].ToLowerInvariant();
                if (memory.Length == 2)
                {
                    _ = ulong.TryParse(memory[1].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
                }
            }

            probes[address] = new Probe(address, register, offset);
        }

        return probes;
    }
}
