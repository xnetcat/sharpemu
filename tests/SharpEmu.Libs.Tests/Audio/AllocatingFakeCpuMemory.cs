// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// A <see cref="FakeCpuMemory"/> that can also satisfy guest allocations, for exports that
/// hand a pointer to their own storage back to the title (such as <c>sceAjmStrError</c>).
/// Allocations are carved out of the top of the region so they cannot collide with the fixed
/// addresses a test writes structures to.
/// </summary>
internal sealed class AllocatingFakeCpuMemory : ICpuMemory, IGuestMemoryAllocator
{
    private readonly FakeCpuMemory _memory;
    private readonly ulong _base;
    private readonly int _size;
    private readonly object _gate = new();
    private ulong _next;

    public AllocatingFakeCpuMemory(ulong baseAddress, int size)
    {
        _memory = new FakeCpuMemory(baseAddress, size);
        _base = baseAddress;
        _size = size;
        // Reserve the upper half of the region for allocations.
        _next = baseAddress + (ulong)(size / 2);
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
        _memory.TryRead(virtualAddress, destination);

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
        _memory.TryWrite(virtualAddress, source);

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        lock (_gate)
        {
            var aligned = alignment <= 1 ? _next : (_next + alignment - 1) / alignment * alignment;
            if (aligned + size > _base + (ulong)_size)
            {
                address = 0;
                return false;
            }

            address = aligned;
            _next = aligned + size;
            return true;
        }
    }

    public bool TryFreeGuestMemory(ulong address) => true;
}
