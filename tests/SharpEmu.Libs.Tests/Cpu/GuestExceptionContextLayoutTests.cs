// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestExceptionContextLayoutTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void Gen5Context_UsesOrbisUcontextLayoutConsumedByUnity()
    {
        var context = CreateContext(Generation.Gen5);
        var continuation = CreateContinuation();

        var bytes = DirectExecutionBackend.BuildGuestExceptionContext(
            context,
            continuation,
            0x500);

        Assert.Equal(0UL, Read64(bytes, 0x08));
        Assert.Equal(continuation.Rdi, Read64(bytes, 0x48));
        Assert.Equal(continuation.Rbx, Read64(bytes, 0x80));
        Assert.Equal(continuation.Rbp, Read64(bytes, 0x88));
        Assert.Equal(continuation.R15, Read64(bytes, 0xB8));
        Assert.Equal(continuation.Rip, Read64(bytes, 0xE0));
        Assert.Equal(continuation.Rflags, Read64(bytes, 0xF0));
        Assert.Equal(continuation.Rsp, Read64(bytes, 0xF8));
        Assert.Equal(0x480UL, Read64(bytes, 0x108));
        Assert.Equal(continuation.FsBase, Read64(bytes, 0x480));
        Assert.Equal(continuation.GsBase, Read64(bytes, 0x488));
    }

    [Fact]
    public void Gen4Context_RetainsUcontextPrefix()
    {
        var context = CreateContext(Generation.Gen4);
        var continuation = CreateContinuation();

        var bytes = DirectExecutionBackend.BuildGuestExceptionContext(
            context,
            continuation,
            0x500);

        Assert.Equal(0UL, Read64(bytes, 0x08));
        Assert.Equal(continuation.Rdi, Read64(bytes, 0x48));
        Assert.Equal(continuation.Rbx, Read64(bytes, 0x80));
        Assert.Equal(continuation.Rbp, Read64(bytes, 0x88));
        Assert.Equal(continuation.R15, Read64(bytes, 0xB8));
        Assert.Equal(continuation.Rip, Read64(bytes, 0xE0));
        Assert.Equal(continuation.Rflags, Read64(bytes, 0xF0));
        Assert.Equal(continuation.Rsp, Read64(bytes, 0xF8));
        Assert.Equal(0x480UL, Read64(bytes, 0x108));
        Assert.Equal(continuation.FsBase, Read64(bytes, 0x480));
        Assert.Equal(continuation.GsBase, Read64(bytes, 0x488));
    }

    [Fact]
    public void ContextWithoutContinuation_CapturesLiveCpuRegistersAndFlags()
    {
        var context = CreateContext(Generation.Gen5);

        var bytes = DirectExecutionBackend.BuildGuestExceptionContext(
            context,
            default,
            0x500);

        Assert.Equal(context[CpuRegister.Rdi], Read64(bytes, 0x48));
        Assert.Equal(context[CpuRegister.Rbx], Read64(bytes, 0x80));
        Assert.Equal(context.Rip, Read64(bytes, 0xE0));
        Assert.Equal(context.Rflags, Read64(bytes, 0xF0));
        Assert.Equal(context[CpuRegister.Rsp], Read64(bytes, 0xF8));
        Assert.Equal(context.FsBase, Read64(bytes, 0x480));
        Assert.Equal(context.GsBase, Read64(bytes, 0x488));
    }

    private static CpuContext CreateContext(Generation generation)
    {
        var context = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), generation)
        {
            Rip = 0x8070_0010,
            Rflags = 0x246,
            FsBase = 0x1234_5000,
            GsBase = 0x6789_A000,
        };
        context[CpuRegister.Rdi] = 0x11;
        context[CpuRegister.Rbx] = 0x22;
        context[CpuRegister.Rsp] = 0x6FFF_FF00;
        return context;
    }

    private static GuestCpuContinuation CreateContinuation() => new()
    {
        Rax = 0x01,
        Rbx = 0x02,
        Rcx = 0x03,
        Rdx = 0x04,
        Rdi = 0x05,
        Rsi = 0x06,
        Rbp = 0x07,
        Rsp = 0x6FFF_EE00,
        R8 = 0x08,
        R9 = 0x09,
        R10 = 0x0A,
        R11 = 0x0B,
        R12 = 0x0C,
        R13 = 0x0D,
        R14 = 0x0E,
        R15 = 0x0F,
        Rip = 0x8070_1234,
        Rflags = 0x202,
        FsBase = 0x1111_0000,
        GsBase = 0x2222_0000,
    };

    private static ulong Read64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)));
}
