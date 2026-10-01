// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

// LOCAL ONLY: distinguish process throttling from codec/guest execution cost.
internal sealed class MacUserActivity : IDisposable
{
    private nint _token;
    private nint _processInfo;

    internal static MacUserActivity? Begin()
    {
        if (!OperatingSystem.IsMacOS() ||
            Environment.GetEnvironmentVariable("SHARPEMU_MAC_USER_ACTIVITY") != "1")
            return null;

        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        try
        {
            var result = new MacUserActivity();
            result._processInfo = Send(GetClass("NSProcessInfo"), Selector("processInfo"));
            var reason = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), "Running guest emulation");
            // NSActivityUserInitiatedAllowingIdleSystemSleep: prevent App Nap
            // during this explicitly requested run, without inhibiting idle sleep.
            result._token = BeginActivity(result._processInfo, Selector("beginActivityWithOptions:reason:"), 0x00FFFFFF, reason);
            if (result._token != 0)
                Send(result._token, Selector("retain"));
            Console.Error.WriteLine($"[LOADER][INFO] macOS user activity active={result._token != 0}");
            return result;
        }
        finally
        {
            Send(pool, Selector("drain"));
        }
    }

    public void Dispose()
    {
        if (_token == 0) return;
        EndActivity(_processInfo, Selector("endActivity:"), _token);
        Send(_token, Selector("release"));
        _token = 0;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint target, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendString(nint target, nint selector, string value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint BeginActivity(nint target, nint selector, ulong options, nint reason);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void EndActivity(nint target, nint selector, nint token);
}
