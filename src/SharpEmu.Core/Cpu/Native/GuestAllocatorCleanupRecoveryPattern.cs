// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

internal static class GuestAllocatorCleanupRecoveryPattern
{
    internal const int FaultCodeLength = 16;
    internal const int EpilogueCodeLength = 15;
    internal const ulong InvalidRecordEpilogueDelta = 0x1F1;
    internal const ulong InvalidMetadataEpilogueDelta = 0x140;

    private static readonly byte[] InvalidRecordSignature =
    [
        0x49, 0x8B, 0x06,             // mov rax, [r14]
        0x48, 0x8B, 0x7D, 0xB8,       // mov rdi, [rbp-48h]
        0x44, 0x8B, 0x2D,             // mov r13d, [rip+disp32]
    ];

    private static readonly byte[] InvalidMetadataSignature =
    [
        0x41, 0x8B, 0x14, 0x9C,       // mov edx, [r12+rbx*4]
        0x89, 0xD1,                   // mov ecx, edx
        0x83, 0xE1, 0x03,             // and ecx, 3
        0x83, 0xF9, 0x01,             // cmp ecx, 1
    ];

    private static readonly byte[] EpilogueSignature =
    [
        0x48, 0x83, 0xC4, 0x48, // add rsp, 48h
        0x5B,                   // pop rbx
        0x41, 0x5C,             // pop r12
        0x41, 0x5D,             // pop r13
        0x41, 0x5E,             // pop r14
        0x41, 0x5F,             // pop r15
        0x5D,                   // pop rbp
        0xC3,                   // ret
    ];

    internal static bool IsInvalidRecordFault(
        ReadOnlySpan<byte> code,
        ulong accessType,
        ulong faultAddress,
        ulong recordAddress) =>
        accessType == 0 &&
        faultAddress != 0 &&
        faultAddress < 0x10000 &&
        faultAddress == recordAddress &&
        code.Length >= InvalidRecordSignature.Length &&
        code[..InvalidRecordSignature.Length].SequenceEqual(InvalidRecordSignature);

    internal static bool IsInvalidMetadataFault(
        ReadOnlySpan<byte> code,
        ulong accessType,
        ulong faultAddress,
        ulong metadataAddress,
        ulong metadataIndex) =>
        accessType == 0 &&
        metadataAddress != 0 &&
        metadataAddress < 0x10000 &&
        metadataIndex < 0x1000 &&
        metadataAddress <= ulong.MaxValue - metadataIndex * sizeof(uint) &&
        faultAddress == metadataAddress + metadataIndex * sizeof(uint) &&
        code.Length >= InvalidMetadataSignature.Length &&
        code[..InvalidMetadataSignature.Length].SequenceEqual(InvalidMetadataSignature);

    internal static bool IsEpilogue(ReadOnlySpan<byte> code) =>
        code.Length >= EpilogueSignature.Length &&
        code[..EpilogueSignature.Length].SequenceEqual(EpilogueSignature);
}
