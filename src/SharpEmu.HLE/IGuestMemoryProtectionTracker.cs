// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Optional guest-memory hook used when an HLE mapping call changes the
/// protection of pages that are also accessed through <see cref="ICpuMemory"/>.
/// </summary>
public interface IGuestMemoryProtectionTracker
{
    void UpdateHostProtection(
        ulong address,
        ulong length,
        bool readable,
        bool writable,
        bool executable);
}
