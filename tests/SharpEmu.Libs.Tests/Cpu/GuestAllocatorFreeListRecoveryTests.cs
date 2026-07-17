// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestAllocatorFreeListRecoveryTests
{
    private static readonly byte[] FreeListPop =
    [
        0x48, 0x8B, 0x01,
        0x48, 0x89, 0x06,
        0x48, 0x89, 0xC8,
        0x48, 0x83, 0xC4, 0x08,
        0x5B,
        0x41, 0x5E,
        0x41, 0x5F,
        0x5D,
        0xC3,
    ];

    [Fact]
    public void ExactFaultingPop_IsRecognized()
    {
        Assert.True(DirectExecutionBackend.IsGuestAllocatorFreeListPop(
            FreeListPop,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0008_01AE_0020));
    }

    [Fact]
    public void DifferentInstructionStream_IsRejected()
    {
        var code = FreeListPop.ToArray();
        code[2] ^= 1;

        Assert.False(DirectExecutionBackend.IsGuestAllocatorFreeListPop(
            code,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0008_01AE_0020));
    }

    [Theory]
    [InlineData(1UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0x8001_5F08UL, 0x8001_5F00UL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0x0000_0FFFUL, 0x0000_0FFFUL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0008_01AE_0021UL)]
    public void NonMatchingFaultContext_IsRejected(
        ulong accessType,
        ulong faultAddress,
        ulong freeListHead,
        ulong freeListSlot)
    {
        Assert.False(DirectExecutionBackend.IsGuestAllocatorFreeListPop(
            FreeListPop,
            accessType,
            faultAddress,
            freeListHead,
            freeListSlot));
    }
}
