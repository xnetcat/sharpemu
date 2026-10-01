// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class ImportStackSnapshotTests
{
    [NativeX64Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void SnapshotPreservesReadableWordsAndTracksGuardChanges(int readableWords)
    {
        var pageSize = (nuint)Environment.SystemPageSize;
        var allocation = (byte*)HostMemory.Alloc(null, pageSize * 2,
            HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
        Assert.NotEqual(0, (nint)allocation);
        try
        {
            var arguments = (ulong*)(allocation + pageSize - (nuint)(readableWords * sizeof(ulong)));
            for (var index = 0; index < 6; index++) arguments[index] = (ulong)(0x100 + index);
            var context = new CpuContext(new FakeCpuMemory(0x10000, 0x1000), Generation.Gen5);
            var pack = (nint)((byte*)arguments - 104);

            DirectExecutionBackend.LoadImportStackArguments(context, pack);
            AssertSnapshot(context, 6);
            Assert.True(HostMemory.Protect(allocation + pageSize, pageSize, HostMemory.PAGE_NOACCESS, out _));
            DirectExecutionBackend.LoadImportStackArguments(context, pack);
            AssertSnapshot(context, readableWords);

            Assert.True(HostMemory.Protect(allocation + pageSize, pageSize, HostMemory.PAGE_READWRITE, out _));
            DirectExecutionBackend.LoadImportStackArguments(context, pack);
            AssertSnapshot(context, 6);
            arguments[0] = 0x999;
            AssertSnapshot(context, 6); // Diagnostics must retain the captured call's arguments.
        }
        finally
        {
            Assert.True(HostMemory.Free(allocation, 0, HostMemory.MEM_RELEASE));
        }
    }

    private static void AssertSnapshot(CpuContext context, int readableWords)
    {
        for (var index = 0; index < 6; index++)
        {
            Assert.True(context.TryGetImportStackArgument(index, out var actual));
            Assert.Equal(index < readableWords ? (ulong)(0x100 + index) : 0UL, actual);
        }
    }
}
