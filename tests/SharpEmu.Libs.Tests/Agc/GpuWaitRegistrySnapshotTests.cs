// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// SnapshotAddressesForMemory is what the wait monitor iterates to re-attempt
// releasing each registered wait's deferred producer, which keeps a
// screen-transition boundary (no further submissions) from wedging forever.
// The registry is process-global; each test snapshots against its own private
// memory key so foreign entries can never satisfy or break its assertions.
public sealed class GpuWaitRegistrySnapshotTests
{
    private static GpuWaitRegistry.WaitingDcb Waiter(object memory, bool is64Bit) => new()
    {
        Memory = memory,
        Is64Bit = is64Bit,
        Mask = uint.MaxValue,
        ReferenceValue = 1,
        CompareFunction = 3,
    };

    [Fact]
    public void SnapshotAddressesForMemory_ReturnsRegisteredWaitPerMemory()
    {
        var memoryA = new object();
        var memoryB = new object();
        var addressA = 0x7020_F776_2000UL;
        var addressB = 0x7020_F776_3000UL;

        GpuWaitRegistry.Register(addressA, Waiter(memoryA, is64Bit: false));
        GpuWaitRegistry.Register(addressB, Waiter(memoryB, is64Bit: false));

        var forA = GpuWaitRegistry.SnapshotAddressesForMemory(memoryA);
        Assert.Contains((addressA, (ulong)sizeof(uint)), forA);
        Assert.DoesNotContain((addressB, (ulong)sizeof(uint)), forA);
    }

    [Fact]
    public void SnapshotAddressesForMemory_ReportsWidestWidthForAddress()
    {
        var memory = new object();
        var address = 0x7020_F776_4000UL;

        // A 32-bit and a 64-bit waiter on the same label: the monitor must probe
        // the 64-bit span so it never re-reads a satisfied 64-bit label as 32.
        GpuWaitRegistry.Register(address, Waiter(memory, is64Bit: false));
        GpuWaitRegistry.Register(address, Waiter(memory, is64Bit: true));

        var snapshot = GpuWaitRegistry.SnapshotAddressesForMemory(memory);
        Assert.Contains((address, (ulong)sizeof(ulong)), snapshot);
    }

    [Fact]
    public void SnapshotAddressesForMemory_EmptyForMemoryWithNoWaits()
    {
        // A private memory key never registered anywhere returns no waits even
        // while other tests hold entries for their own keys.
        var memory = new object();
        Assert.Empty(GpuWaitRegistry.SnapshotAddressesForMemory(memory));
    }
}
