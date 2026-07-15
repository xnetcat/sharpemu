// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Provides a stable identity for state shared by multiple CPU-memory views of
/// the same guest address space.
/// </summary>
public interface ICpuMemoryStateKeyProvider
{
    object CpuMemoryStateKey { get; }
}
