// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

// LOCAL ONLY: compare launchd's default process policy with application policy.
internal static class MacApplicationPolicy
{
    private const string AppliedVariable = "SHARPEMU_MAC_APP_POLICY_APPLIED";

    internal static void ReexecIfNeeded(string[] args)
    {
        if (!OperatingSystem.IsMacOS() ||
            Environment.GetEnvironmentVariable("SHARPEMU_MAC_APP_POLICY") != "1" ||
            Environment.GetEnvironmentVariable(AppliedVariable) == "1")
            return;

        const string taskPolicy = "/usr/sbin/taskpolicy";
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(taskPolicy)) return;

        var environment = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (!string.Equals(entry.Key.ToString(), AppliedVariable, StringComparison.Ordinal))
                environment.Add($"{entry.Key}={entry.Value}");
        environment.Add($"{AppliedVariable}=1");
        var argv = new List<string> { taskPolicy, "-a", executable };
        argv.AddRange(args);
        Console.Error.WriteLine("[LOADER][INFO] Restarting with macOS application scheduling policy");
        _ = Execve(taskPolicy, ToPointers(argv), ToPointers(environment));
        Console.Error.WriteLine($"[LOADER][WARN] macOS application policy restart failed: errno={Marshal.GetLastPInvokeError()}");
    }

    private static nint[] ToPointers(IReadOnlyList<string> values)
    {
        var pointers = new nint[values.Count + 1];
        for (var index = 0; index < values.Count; index++)
            pointers[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
        return pointers;
    }

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int Execve(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint[] argv, nint[] envp);
}
