// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AprAsyncExecutionTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private readonly FakeCpuMemory _memory = new(Base, 12 * 1024 * 1024);
    private CpuContext Context() => new(_memory, Generation.Gen5);
    public AprAsyncExecutionTests() => AmprFileRegistry.ClearForTests();
    public void Dispose()
    {
        KernelAprCompatExports.BeginShutdown();
        Assert.True(KernelAprCompatExports.Drain(TimeSpan.FromSeconds(3)));
        KernelAprCompatExports.BeginSession();
    }

    private CpuContext Buffer(int index)
    {
        var ctx = Context();
        ctx[CpuRegister.Rdi] = Base + 0x1000u + (ulong)index * 0x200;
        ctx[CpuRegister.Rsi] = ctx[CpuRegister.Rdi] + 0x40;
        ctx[CpuRegister.Rdx] = 0x180;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(ctx));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(ctx));
        return ctx;
    }

    private static void Write(CpuContext ctx, ulong address, ulong value)
    {
        ctx[CpuRegister.Rsi] = address; ctx[CpuRegister.Rdx] = value;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(ctx));
    }

    private static void Wait(CpuContext ctx, ulong address)
    {
        ctx[CpuRegister.Rsi] = address; ctx[CpuRegister.Rdx] = 1; ctx[CpuRegister.Rcx] = 0; ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, AmprExports.CommandBufferWaitOnAddress(ctx));
    }

    private uint Submit(CpuContext ctx, ulong priority = 3, ulong result = 0)
    {
        ctx[CpuRegister.Rsi] = priority;
        ctx[CpuRegister.Rdx] = result;
        ctx[CpuRegister.Rcx] = Base + 0x100;
        Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetResult(ctx));
        Assert.True(ctx.TryReadUInt32(Base + 0x100, out var id));
        return id;
    }

    private async Task Complete(uint id)
    {
        var ctx = Context(); ctx[CpuRegister.Rdi] = id;
        var result = await Task.Run(() => KernelAprCompatExports.KernelAprWaitCommandBuffer(ctx)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, result);
        Assert.NotEqual(0, KernelAprCompatExports.KernelAprWaitCommandBuffer(ctx));
    }

    [Fact]
    public async Task WaitsYieldWorkersAndPreserveCapturedCommandOrder()
    {
        var ids = new List<uint>();
        for (var i = 0; i < 8; i++)
        {
            var ctx = Buffer(i);
            Wait(ctx, Base + 0x200);
            Write(ctx, Base + 0x300u + (ulong)i * 8, 42);
            ids.Add(Submit(ctx, 1));
            Assert.True(ctx.TryReadUInt64(Base + 0x300u + (ulong)i * 8, out var value));
            Assert.Equal(0ul, value);
            // Reusing the guest builder must not alter an in-flight snapshot.
            _ = Buffer(i);
            Write(ctx, Base + 0x300u + (ulong)i * 8, 99);
        }
        var producer = Buffer(9);
        Write(producer, Base + 0x200, 1);
        await Complete(Submit(producer, 6));
        foreach (var id in ids) await Complete(id);
        for (var i = 0; i < 8; i++)
        {
            Assert.True(producer.TryReadUInt64(Base + 0x300u + (ulong)i * 8, out var value));
            Assert.Equal(42ul, value);
        }
    }

    [Fact]
    public async Task ErrorResultIsPublishedAfterPriorWritesAndSkipsLaterCommands()
    {
        var ctx = Buffer(0);
        Write(ctx, Base + 0x300, 11);
        Write(ctx, Base + 20 * 1024 * 1024, 22); // Unmapped guest address.
        Write(ctx, Base + 0x300, 33);
        await Complete(Submit(ctx, result: Base + 0x400));
        Assert.True(ctx.TryReadUInt64(Base + 0x300, out var marker));
        Assert.Equal(11ul, marker);
        Assert.True(ctx.TryReadUInt32(Base + 0x400, out var result));
        Assert.Equal(unchecked((uint)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT), result);
        Assert.True(ctx.TryReadUInt32(Base + 0x404, out var offset));
        Assert.Equal(0x20u, offset);
    }

    [Fact]
    public async Task BoundedQueueRejectsExcessAndShutdownCompletesAddressWaits()
    {
        var ctx = Buffer(0);
        Wait(ctx, Base + 0x200);
        var ids = Enumerable.Range(0, AprExecutor.Capacity).Select(_ => Submit(ctx)).ToArray();
        ctx[CpuRegister.Rsi] = 3; ctx[CpuRegister.Rdx] = Base + 0x100;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetId(ctx));
        KernelAprCompatExports.BeginShutdown();
        Assert.True(KernelAprCompatExports.Drain(TimeSpan.FromSeconds(3)));
        foreach (var id in ids) await Complete(id);
    }

    [Fact]
    public async Task ReadAheadCopiesLargeReadBeforeCompletionMarker()
    {
        var path = Path.GetTempFileName();
        try
        {
            var contents = new byte[9 * 1024 * 1024 + 17];
            new System.Random(42).NextBytes(contents);
            File.WriteAllBytes(path, contents);
            AmprFileRegistry.RegisterApp0RelativeForTests("async-read.bin", path);
            var fileId = AmprFileRegistry.ComputeFileId("async-read.bin");
            var ctx = Buffer(0);
            var destination = Base + 0x10000;
            ctx[CpuRegister.Rsp] = Base + 0x800;
            Assert.True(ctx.TryWriteUInt64(Base + 0x808, 0));
            ctx[CpuRegister.Rcx] = fileId; ctx[CpuRegister.R8] = destination; ctx[CpuRegister.R9] = (ulong)contents.Length;
            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(ctx));
            Write(ctx, Base + 0x300, 1);
            await Complete(Submit(ctx, result: Base + 0x400));
            var actual = new byte[contents.Length];
            Assert.True(_memory.TryRead(destination, actual));
            Assert.Equal(contents, actual);
            Assert.True(ctx.TryReadUInt64(Base + 0x300, out var marker));
            Assert.Equal(1ul, marker);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task TerminalMarkerObservesPublishedResult()
    {
        var ctx = Buffer(0);
        Write(ctx, Base + 0x300, 1);
        Assert.True(ctx.TryWriteUInt64(Base + 0x400, ulong.MaxValue));
        uint observedResult = uint.MaxValue;
        var observing = new MarkerMemory(_memory, Base + 0x300, () =>
        {
            Assert.True(ctx.TryReadUInt32(Base + 0x400, out observedResult));
        });
        var submit = new CpuContext(observing, Generation.Gen5);
        submit[CpuRegister.Rdi] = ctx[CpuRegister.Rdi];
        await Complete(Submit(submit, result: Base + 0x400));
        Assert.Equal(0u, observedResult);
    }

    private sealed class MarkerMemory(ICpuMemory inner, ulong marker, Action observe) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => inner.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address == marker) observe();
            return inner.TryWrite(address, source);
        }
    }

}
