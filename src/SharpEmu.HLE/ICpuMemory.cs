// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface ICpuMemory
{
    bool TryRead(ulong virtualAddress, Span<byte> destination);

    bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source);

    bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) => false;

    /// <summary>
    /// Reports whether a complete range can be read without copying it. Memory
    /// implementations that cannot validate ranges cheaply may return false.
    /// </summary>
    bool IsRangeReadable(ulong virtualAddress, int length) => false;
}
