// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

internal static class GuestAllocatorFreeListRecoveryPattern
{
    internal const int LeafPopCodeLength = 20;
    internal const int InlinedPopCodeLength = 23;

    private static readonly byte[] LeafPopSignature =
    [
        0x48, 0x8B, 0x01,       // mov rax, [rcx]
        0x48, 0x89, 0x06,       // mov [rsi], rax
        0x48, 0x89, 0xC8,       // mov rax, rcx
        0x48, 0x83, 0xC4, 0x08, // add rsp, 8
        0x5B,                   // pop rbx
        0x41, 0x5E,             // pop r14
        0x41, 0x5F,             // pop r15
        0x5D,                   // pop rbp
        0xC3,                   // ret
    ];

    private static readonly byte[] InlinedPopSignature =
    [
        0x48, 0x8B, 0x01,             // mov rax, [rcx]
        0x48, 0x85, 0xC0,             // test rax, rax
        0x0F, 0x84, 0xC9, 0, 0, 0,    // je allocator slow path
        0x41, 0xFF, 0x4C, 0x3E, 0x08, // dec dword ptr [r14+rdi+8]
        0x48, 0x8B, 0x10,             // mov rdx, [rax] (fault)
        0x48, 0x89, 0x11,             // mov [rcx], rdx
    ];

    internal static bool IsLeafPop(
        ReadOnlySpan<byte> code,
        ulong accessType,
        ulong faultAddress,
        ulong freeListHead,
        ulong freeListSlot) =>
        accessType == 0 &&
        faultAddress != 0 &&
        faultAddress == freeListHead &&
        freeListSlot >= 0x0000000800000000UL &&
        (freeListSlot & 7) == 0 &&
        code.SequenceEqual(LeafPopSignature);

    internal static bool IsInlinedPop(
        ReadOnlySpan<byte> code,
        ulong accessType,
        ulong faultAddress,
        ulong freeListHead,
        ulong freeListSlot,
        ulong bucketBase,
        ulong bucketOffset) =>
        accessType == 0 &&
        faultAddress != 0 &&
        faultAddress == freeListHead &&
        freeListSlot >= 0x0000000800000000UL &&
        (freeListSlot & 7) == 0 &&
        (bucketOffset & 7) == 0 &&
        bucketOffset <= 0x1000 &&
        bucketBase <= ulong.MaxValue - bucketOffset &&
        bucketBase + bucketOffset == freeListSlot &&
        code.SequenceEqual(InlinedPopSignature);
}
