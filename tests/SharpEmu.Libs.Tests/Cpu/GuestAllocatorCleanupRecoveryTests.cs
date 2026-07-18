// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestAllocatorCleanupRecoveryTests
{
    private static readonly byte[] InvalidRecordCode =
    [
        0x49, 0x8B, 0x06, 0x48, 0x8B, 0x7D, 0xB8, 0x44,
        0x8B, 0x2D, 0x43, 0x52, 0x9B, 0x06, 0x4C, 0x89,
    ];

    private static readonly byte[] InvalidMetadataCode =
    [
        0x41, 0x8B, 0x14, 0x9C, 0x89, 0xD1, 0x83, 0xE1,
        0x03, 0x83, 0xF9, 0x01, 0x74, 0x29, 0x48, 0x8D,
    ];

    private static readonly byte[] EpilogueCode =
    [
        0x48, 0x83, 0xC4, 0x48, 0x5B, 0x41, 0x5C, 0x41,
        0x5D, 0x41, 0x5E, 0x41, 0x5F, 0x5D, 0xC3,
    ];

    [Fact]
    public void InvalidRecordRead_IsRecognized()
    {
        Assert.True(GuestAllocatorCleanupRecoveryPattern.IsInvalidRecordFault(
            InvalidRecordCode, 0, 1, 1));
    }

    [Theory]
    [InlineData(1UL, 2UL)]
    [InlineData(0x10000UL, 0x10000UL)]
    public void InvalidRecordRead_RejectsUnrelatedOrMappedTargets(
        ulong faultAddress,
        ulong recordAddress)
    {
        Assert.False(GuestAllocatorCleanupRecoveryPattern.IsInvalidRecordFault(
            InvalidRecordCode, 0, faultAddress, recordAddress));
    }

    [Fact]
    public void InvalidMetadataRead_IsRecognized()
    {
        Assert.True(GuestAllocatorCleanupRecoveryPattern.IsInvalidMetadataFault(
            InvalidMetadataCode, 0, 0x114, 0x100, 5));
    }

    [Theory]
    [InlineData(0x115UL, 0x100UL, 5UL)]
    [InlineData(0x4000UL, 0x100UL, 0x1000UL)]
    [InlineData(0x10014UL, 0x10000UL, 5UL)]
    public void InvalidMetadataRead_RejectsUnrelatedOrBroadTargets(
        ulong faultAddress,
        ulong metadataAddress,
        ulong metadataIndex)
    {
        Assert.False(GuestAllocatorCleanupRecoveryPattern.IsInvalidMetadataFault(
            InvalidMetadataCode,
            0,
            faultAddress,
            metadataAddress,
            metadataIndex));
    }

    [Fact]
    public void CleanupEpilogue_IsRecognizedExactly()
    {
        Assert.True(GuestAllocatorCleanupRecoveryPattern.IsEpilogue(EpilogueCode));
        var altered = EpilogueCode.ToArray();
        altered[0] ^= 1;
        Assert.False(GuestAllocatorCleanupRecoveryPattern.IsEpilogue(altered));
    }
}
