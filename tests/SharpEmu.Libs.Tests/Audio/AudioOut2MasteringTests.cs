// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// Covers the AudioOut2 mastering chain lifecycle. SharpEmu applies no mastering, but the
/// handle it hands back has to round-trip so a title's SetParam/Term calls succeed and a
/// mismatched handle is still rejected.
/// </summary>
public sealed class AudioOut2MasteringTests
{
    private const int InvalidArgument = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
    private const int MemoryFault = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong ParamAddress = MemoryBase + 0x200;
    private const ulong HandleAddress = MemoryBase + 0x300;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public AudioOut2MasteringTests() => _ctx = new CpuContext(_memory, Generation.Gen5);

    [Theory]
    [InlineData("XHl38ZNknbs", "sceAudioOut2MasteringInit")]
    [InlineData("v8iOE+j8a5o", "sceAudioOut2MasteringSetParam")]
    [InlineData("2bbBBOkH4CY", "sceAudioOut2MasteringTerm")]
    public void MasteringExports_ResolveForGen5(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export), $"{exportName} unresolved");
        Assert.Equal(exportName, export.Name);
    }

    [Fact]
    public void MasteringLifecycle_InitPublishesAHandleThatSetParamAndTermAccept()
    {
        var handle = Init(contextHandle: 0x1234);

        Assert.NotEqual(0ul, handle);
        Assert.Equal(0, SetParam(handle));
        Assert.Equal(0, Term(handle));

        // The chain is gone; a second teardown must not succeed.
        Assert.Equal(InvalidArgument, Term(handle));
        Assert.Equal(InvalidArgument, SetParam(handle));
    }

    [Fact]
    public void MasteringInit_RejectsAMissingContext()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ParamAddress;
        _ctx[CpuRegister.Rdx] = HandleAddress;

        Assert.Equal(InvalidArgument, AudioOut2Exports.AudioOut2MasteringInit(_ctx));
    }

    [Fact]
    public void MasteringInit_ReportsAFaultingHandleOutput()
    {
        _ctx[CpuRegister.Rdi] = 0x4321;
        _ctx[CpuRegister.Rsi] = ParamAddress;
        _ctx[CpuRegister.Rdx] = MemoryBase + 0x1_0000;

        Assert.Equal(MemoryFault, AudioOut2Exports.AudioOut2MasteringInit(_ctx));
    }

    [Fact]
    public void MasteringSetParamAndTerm_RejectHandlesTheyNeverIssued()
    {
        Assert.Equal(InvalidArgument, SetParam(0));
        Assert.Equal(InvalidArgument, SetParam(0xDEAD_BEEF));
        Assert.Equal(InvalidArgument, Term(0));
        Assert.Equal(InvalidArgument, Term(0xDEAD_BEEF));
    }

    private ulong Init(ulong contextHandle)
    {
        _ctx[CpuRegister.Rdi] = contextHandle;
        _ctx[CpuRegister.Rsi] = ParamAddress;
        _ctx[CpuRegister.Rdx] = HandleAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringInit(_ctx));

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(HandleAddress, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private int SetParam(ulong handle)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = ParamAddress;
        return AudioOut2Exports.AudioOut2MasteringSetParam(_ctx);
    }

    private int Term(ulong handle)
    {
        _ctx[CpuRegister.Rdi] = handle;
        return AudioOut2Exports.AudioOut2MasteringTerm(_ctx);
    }
}
