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

    private static readonly byte[] InlinedFreeListPop =
    [
        0x48, 0x8B, 0x01,
        0x48, 0x85, 0xC0,
        0x0F, 0x84, 0xC9, 0, 0, 0,
        0x41, 0xFF, 0x4C, 0x3E, 0x08,
        0x48, 0x8B, 0x10,
        0x48, 0x89, 0x11,
    ];

    [Fact]
    public void ExactFaultingPop_IsRecognized()
    {
        Assert.True(GuestAllocatorFreeListRecoveryPattern.IsLeafPop(
            FreeListPop,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0008_01AE_0020));
    }

    [Fact]
    public void TinyReleaseLabelValue_IsRecognizedAsCorruptHead()
    {
        Assert.True(GuestAllocatorFreeListRecoveryPattern.IsLeafPop(
            FreeListPop,
            accessType: 0,
            faultAddress: 1,
            freeListHead: 1,
            freeListSlot: 0x0000_0008_01AE_0020));
    }

    [Fact]
    public void DifferentInstructionStream_IsRejected()
    {
        var code = FreeListPop.ToArray();
        code[2] ^= 1;

        Assert.False(GuestAllocatorFreeListRecoveryPattern.IsLeafPop(
            code,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0008_01AE_0020));
    }

    [Theory]
    [InlineData(1UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0x8001_5F08UL, 0x8001_5F00UL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0UL, 0UL, 0x0000_0008_01AE_0020UL)]
    [InlineData(0UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0008_01AE_0021UL)]
    public void NonMatchingFaultContext_IsRejected(
        ulong accessType,
        ulong faultAddress,
        ulong freeListHead,
        ulong freeListSlot)
    {
        Assert.False(GuestAllocatorFreeListRecoveryPattern.IsLeafPop(
            FreeListPop,
            accessType,
            faultAddress,
            freeListHead,
            freeListSlot));
    }

    [Fact]
    public void ExactInlinedFaultingPop_IsRecognized()
    {
        Assert.True(GuestAllocatorFreeListRecoveryPattern.IsInlinedPop(
            InlinedFreeListPop,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0080_01AE_0020,
            bucketBase: 0x0000_0080_01AE_0000,
            bucketOffset: 0x20));
    }

    [Fact]
    public void TinyReleaseLabelValue_InInlinedPop_IsRecognizedAsCorruptHead()
    {
        Assert.True(GuestAllocatorFreeListRecoveryPattern.IsInlinedPop(
            InlinedFreeListPop,
            accessType: 0,
            faultAddress: 1,
            freeListHead: 1,
            freeListSlot: 0x0000_0080_01AE_0020,
            bucketBase: 0x0000_0080_01AE_0000,
            bucketOffset: 0x20));
    }

    [Fact]
    public void DifferentInlinedInstructionStream_IsRejected()
    {
        var code = InlinedFreeListPop.ToArray();
        code[8] ^= 1;

        Assert.False(GuestAllocatorFreeListRecoveryPattern.IsInlinedPop(
            code,
            accessType: 0,
            faultAddress: 0x8001_5F00,
            freeListHead: 0x8001_5F00,
            freeListSlot: 0x0000_0080_01AE_0020,
            bucketBase: 0x0000_0080_01AE_0000,
            bucketOffset: 0x20));
    }

    [Theory]
    [InlineData(1UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0080_01AE_0000UL, 0x20UL)]
    [InlineData(0UL, 0x8001_5F08UL, 0x8001_5F00UL, 0x0000_0080_01AE_0000UL, 0x20UL)]
    [InlineData(0UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0080_01AE_0000UL, 0x28UL)]
    [InlineData(0UL, 0x8001_5F00UL, 0x8001_5F00UL, 0x0000_0080_01AE_0000UL, 0x20UL)]
    public void NonMatchingInlinedFaultContext_IsRejected(
        ulong accessType,
        ulong faultAddress,
        ulong freeListHead,
        ulong bucketBase,
        ulong bucketOffset)
    {
        ulong freeListSlot = bucketBase + 0x20;
        if (accessType == 0 &&
            faultAddress == freeListHead &&
            bucketOffset == 0x20 &&
            bucketBase == 0x0000_0080_01AE_0000)
        {
            freeListSlot++;
        }

        Assert.False(GuestAllocatorFreeListRecoveryPattern.IsInlinedPop(
            InlinedFreeListPop,
            accessType,
            faultAddress,
            freeListHead,
            freeListSlot,
            bucketBase,
            bucketOffset));
    }
}
