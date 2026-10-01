// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class SmallMemsetDispatchTests
{
    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(1, 0x1234UL)]
    [InlineData(17, 0UL)]
    [InlineData(255, ulong.MaxValue)]
    [InlineData(256, 0x180UL)]
    public void SmallClearMatchesExportAndPreservesSurroundingBytes(int count, ulong value)
    {
        var fast = new FakeCpuMemory(0x10000, 512);
        var reference = new FakeCpuMemory(0x10000, 512);
        var sentinel = Enumerable.Repeat((byte)0xCC, 512).ToArray();
        Assert.True(fast.TryWrite(0x10000, sentinel));
        Assert.True(reference.TryWrite(0x10000, sentinel));
        var context = new CpuContext(reference, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x10013;
        context[CpuRegister.Rsi] = value;
        context[CpuRegister.Rdx] = (ulong)count;
        Assert.Equal(0, KernelMemoryCompatExports.Memset(context));
        Assert.True(DirectExecutionBackend.TryWriteSmallMemset(fast, 0x10013, value, (ulong)count));
        var actual = new byte[512];
        var expected = new byte[512];
        Assert.True(fast.TryRead(0x10000, actual));
        Assert.True(reference.TryRead(0x10000, expected));
        Assert.Equal(expected, actual);
        Assert.Equal(0x10013UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(0xFFFUL, 1UL)]
    [InlineData(0x800000000000UL, 1UL)]
    [InlineData(0x10000UL, 257UL)]
    [InlineData(0x10000UL, ulong.MaxValue)]
    [InlineData(0x101FFUL, 2UL)]
    public void UnsupportedCallsFallBackWithoutChangingMemory(ulong destination, ulong count)
    {
        var memory = new FakeCpuMemory(0x10000, 512);
        Assert.False(DirectExecutionBackend.TryWriteSmallMemset(memory, destination, 0xFF, count));
        var actual = new byte[512];
        Assert.True(memory.TryRead(0x10000, actual));
        Assert.All(actual, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ZeroLengthAcceptsAnyDestinationWithoutWriting()
    {
        Assert.True(DirectExecutionBackend.TryWriteSmallMemset(null!, ulong.MaxValue, 1, 0));
    }

    [Fact]
    public void MemsetUsesNonblockingLeafDispatch()
    {
        var backend = RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        Assert.True((bool)typeof(DirectExecutionBackend).GetMethod("IsLeafImport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(backend, ["8zTFvBIAIN8"])!);
        Assert.True((bool)typeof(DirectExecutionBackend).GetMethod("IsNoBlockLeafImport", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["8zTFvBIAIN8"])!);
    }
}
