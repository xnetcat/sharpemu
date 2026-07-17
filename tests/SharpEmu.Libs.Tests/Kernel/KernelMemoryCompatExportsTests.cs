// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelMemoryCompatExportsTests
{
    [Fact]
    public void DirectMemoryQuery_FindNextReturnsFollowingAllocation()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong allocationOutAddress = memoryBase + 0x100;
        const ulong queryInfoAddress = memoryBase + 0x200;
        const ulong searchStart = 0x3FFF_0000_0;
        const ulong searchEnd = 0x4_0000_0000;
        const ulong allocationLength = 0x4000;
        const int memoryType = 0x12;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = allocationLength;
        context[CpuRegister.Rcx] = allocationLength;
        context[CpuRegister.R8] = memoryType;
        context[CpuRegister.R9] = allocationOutAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(allocationOutAddress, out var allocationStart));

        try
        {
            context[CpuRegister.Rdi] = allocationStart - allocationLength;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = queryInfoAddress;
            context[CpuRegister.Rcx] = 24;

            var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

            Assert.Equal(0, result);
            Assert.True(context.TryReadUInt64(queryInfoAddress, out var queriedStart));
            Assert.True(context.TryReadUInt64(queryInfoAddress + 8, out var queriedEnd));
            Assert.Equal(allocationStart, queriedStart);
            Assert.Equal(allocationStart + allocationLength, queriedEnd);
        }
        finally
        {
            context[CpuRegister.Rdi] = allocationStart;
            context[CpuRegister.Rsi] = allocationLength;
            Assert.Equal(0, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));
        }
    }

    [Fact]
    public void VirtualQuery_FindNextPastAddressSpaceReturnsAccessDenied()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong queryInfoAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = queryInfoAddress;
        context[CpuRegister.Rcx] = 72;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_ACCESS_DENIED, result);
    }

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
