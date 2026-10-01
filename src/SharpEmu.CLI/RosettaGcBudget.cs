// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

/// <summary>
/// Rosetta can assert while a GC suspension reads translated thread registers
/// through FlushProcessWriteBuffers. Reducing collection frequency mitigates
/// that failure; it does not repair Rosetta's register-state handling.
/// The gen0 budget alone is insufficient when the heap segment size constrains
/// it. Configure both, and keep medium-sized streaming buffers off the LOH.
/// </summary>
/// <remarks>
/// The GC reads these settings when the runtime starts, so the process
/// re-executes itself once with missing settings filled in. execve keeps the process id.
/// </remarks>
internal static class RosettaGcBudget
{
    private static readonly (string Name, string Value)[] Defaults =
    [
        ("DOTNET_GCgen0size", "0x40000000"), // 1 GiB
        ("DOTNET_GCSegmentSize", "0x80000000"), // 2 GiB
        ("DOTNET_GCGen0MaxBudget", "0x40000000"),
        ("DOTNET_GCLOHThreshold", "0x400000"), // 4 MiB
    ];
    private const string OptOutVariable = "SHARPEMU_ROSETTA_GC_BUDGET";

    public static void ReexecIfNeeded(string[] args)
    {
        if (!OperatingSystem.IsMacOS() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            Environment.GetEnvironmentVariable(OptOutVariable) == "0" ||
            !IsTranslated())
        {
            return;
        }

        // Preserve each user override, and stop re-executing once all settings
        // are present. The opt-out disables the entire policy.
        var missing = Defaults.Where(setting => Environment.GetEnvironmentVariable(setting.Name) is null).ToArray();
        if (missing.Length == 0) return;

        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var environment = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add($"{entry.Key}={entry.Value}");
        }
        foreach (var setting in missing)
            environment.Add($"{setting.Name}={setting.Value}");

        var argv = new string[args.Length + 1];
        argv[0] = path;
        args.CopyTo(argv, 1);

        // Only returns on failure; the emulator then runs with the default budget.
        _ = Execve(path, ToNullTerminated(argv), ToNullTerminated(environment));
        Console.Error.WriteLine(
            $"[LOADER][WARN] Could not restart with a larger GC budget under Rosetta: errno={Marshal.GetLastPInvokeError()}");
    }

    private static bool IsTranslated()
    {
        var value = 0;
        var size = (nint)sizeof(int);
        return SysctlByName("sysctl.proc_translated", ref value, ref size, 0, 0) == 0 && value == 1;
    }

    private static nint[] ToNullTerminated(IReadOnlyList<string> values)
    {
        var pointers = new nint[values.Count + 1];
        for (var i = 0; i < values.Count; i++)
        {
            pointers[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        }
        return pointers;
    }

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int Execve(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        nint[] argv,
        nint[] envp);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref int value,
        ref nint size,
        nint newValue,
        nint newSize);
}
