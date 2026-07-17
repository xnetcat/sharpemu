// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpUniversalDataSystemExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void ArraySetString_ConsoleLayout_ValidatesArrayAndValue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var arrayAddress = memory.WriteCString(BaseAddress + 0x100, "array");
        var valueAddress = memory.WriteCString(BaseAddress + 0x200, "value");
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = arrayAddress;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = valueAddress;

        var result = NpUniversalDataSystemExports
            .NpUniversalDataSystemEventPropertyArraySetString(ctx);

        Assert.Equal(0, result);
    }

    [Fact]
    public void ArraySetString_PcLayout_IgnoresStaleRcx()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var arrayAddress = memory.WriteCString(BaseAddress + 0x100, "array");
        var valueAddress = memory.WriteCString(BaseAddress + 0x200, "value");
        ctx[CpuRegister.Rdi] = arrayAddress;
        ctx[CpuRegister.Rsi] = valueAddress;
        ctx[CpuRegister.Rcx] = BaseAddress + 0x2000;

        var result = NpUniversalDataSystemExports
            .NpUniversalDataSystemEventPropertyArraySetString(ctx);

        Assert.Equal(0, result);
    }
}
