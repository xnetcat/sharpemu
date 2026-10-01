// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelAprCompatExports
{
    private static readonly ConcurrentDictionary<uint, AprSubmission> _submittedCommandBuffers = new();
    private static int _nextSubmissionId;
    private static int _aprWaitTraceCount;
    private static readonly bool _traceApr =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal);

    private static readonly AprCompletionRanges _completedIds = new();
    private static readonly object _executorGate = new();
    private static AprExecutor? _executor;
    private static bool _shuttingDown;

    private sealed class AprSubmission(ulong commandBuffer)
    {
        internal readonly ulong CommandBuffer = commandBuffer;
        internal readonly ManualResetEventSlim Completed = new(false);
        internal int PublicationResult;
        internal bool Claimed;
    }

    [SysAbiExport(Nid = "ASoW5WE-UPo", ExportName = "sceKernelAprSubmitCommandBufferAndGetResult",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetResult(CpuContext ctx) =>
        Submit(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], true);

    [SysAbiExport(Nid = "eE4Szl8sil8", ExportName = "sceKernelAprSubmitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBuffer(CpuContext ctx) => Submit(ctx, 0, 0, false);

    [SysAbiExport(Nid = "qvMUCyyaCSI", ExportName = "sceKernelAprSubmitCommandBufferAndGetId",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetId(CpuContext ctx) =>
        Submit(ctx, 0, ctx[CpuRegister.Rdx], true);

    private static int Submit(CpuContext ctx, ulong resultAddress, ulong outId, bool retainId)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var priority = ctx[CpuRegister.Rsi];
        if (priority is < 1 or > 6 || (retainId && outId == 0))
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        var captureResult = AmprExports.CaptureCommandBuffer(ctx, commandBuffer, out var batch);
        if (captureResult != 0) return captureResult;
        var publicationContext = new CpuContext(ctx.Memory, ctx.TargetGeneration);
        var submission = new AprSubmission(commandBuffer);
        lock (_executorGate)
        {
            if (_shuttingDown) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
            var id = 0u;
            if (retainId)
            {
                do { id = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId)); }
                while (id == 0 || _submittedCommandBuffers.ContainsKey(id) || _completedIds.Contains(id));
            }
            _executor ??= new AprExecutor();
            var publishFailed = false;
            var resultPublished = false;
            void PublishResult(int result, uint offset)
            {
                if (resultPublished && result == 0) return;
                try
                {
                    if (resultAddress != 0 && !TryWriteAprResult(publicationContext, resultAddress, result, offset))
                        submission.PublicationResult = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
                catch (Exception)
                {
                    submission.PublicationResult = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
                resultPublished = true;
            }
            batch!.BeforeFinalSignal = () => PublishResult(0, 0);
            var work = new AprExecutor.Work(batch, priority, (result, offset) =>
            {
                try { PublishResult(result, offset); }
                finally
                {
                    lock (_executorGate)
                    {
                        if (retainId && !submission.Claimed)
                        {
                            _submittedCommandBuffers.TryRemove(id, out _);
                            _completedIds.Add(id, submission.PublicationResult);
                        }
                        submission.Completed.Set();
                    }
                }
            });
            if (!_executor.TrySubmit(work, () =>
                {
                    if (retainId && !ctx.TryWriteUInt32(outId, id)) { publishFailed = true; return false; }
                    if (retainId) _submittedCommandBuffers[id] = submission;
                    return true;
                }))
                return publishFailed ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            TraceApr(ctx, "submit_async", id, commandBuffer, priority, resultAddress);
        }
        return 0;
    }

    [SysAbiExport(Nid = "rqwFKI4PAiM", ExportName = "sceKernelAprWaitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAprWaitCommandBuffer(CpuContext ctx)
    {
        var id = unchecked((uint)ctx[CpuRegister.Rdi]);
        AprSubmission submission;
        lock (_executorGate)
        {
            if (_completedIds.TryTake(id, out var result)) return result;
            if (!_submittedCommandBuffers.TryRemove(id, out submission!))
            {
                TraceAprWaitFailure(ctx, "wait_missing", id, 0, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
            submission.Claimed = true;
        }
        submission.Completed.Wait();
        TraceApr(ctx, "wait", id, submission.CommandBuffer, 0, 0);
        return submission.PublicationResult;
    }

    public static void BeginShutdown()
    {
        lock (_executorGate) { _shuttingDown = true; _executor?.Stop(); }
    }

    public static bool Drain(TimeSpan timeout)
    {
        AprExecutor? executor;
        lock (_executorGate) executor = _executor;
        return executor is null || executor.Join(timeout);
    }

    public static void BeginSession()
    {
        lock (_executorGate)
        {
            if (_executor is not null)
            {
                if (!_shuttingDown || !_executor.Join(TimeSpan.Zero)) return;
                _executor = null;
                foreach (var submission in _submittedCommandBuffers.Values) submission.Completed.Dispose();
                _submittedCommandBuffers.Clear();
                _completedIds.Clear();
            }
            _shuttingDown = false;
        }
    }

    private static bool TryWriteAprResult(
        CpuContext ctx,
        ulong resultAddress,
        int executionResult,
        uint errorOffset)
    {
        Span<byte> result = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteInt32LittleEndian(result, executionResult);
        BinaryPrimitives.WriteUInt32LittleEndian(result[sizeof(int)..], errorOffset);
        return ctx.Memory.TryWrite(resultAddress, result);
    }

    private static void TraceApr(
        CpuContext ctx,
        string operation,
        uint submissionId,
        ulong commandBuffer,
        ulong priority,
        ulong aux)
    {
        if (!_traceApr)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}: id=0x{submissionId:X8} cmd=0x{commandBuffer:X16} priority=0x{priority:X16} aux=0x{aux:X16} ret=0x{returnRip:X16}");
        if (aux != 0 &&
            ctx.TryReadUInt64(aux, out var result0) &&
            ctx.TryReadUInt64(aux + sizeof(ulong), out var result1))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] apr.{operation}.result: addr=0x{aux:X16} q0=0x{result0:X16} q1=0x{result1:X16}");
        }
    }

    private static void TraceAprWaitFailure(
        CpuContext ctx,
        string operation,
        uint submissionId,
        ulong commandBuffer,
        ulong priority,
        ulong resultAddress)
    {
        if (!_traceApr)
        {
            return;
        }

        var traceCount = Interlocked.Increment(ref _aprWaitTraceCount);
        if (traceCount > 32 && (traceCount & 0x3FF) != 0)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}: id=0x{submissionId:X8} cmd=0x{commandBuffer:X16} " +
            $"rsi=0x{priority:X16} rdx=0x{resultAddress:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} " +
            $"r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16} ret=0x{returnRip:X16}");
        TraceReadableQword(ctx, operation, "rsi", priority);
        TraceReadableQword(ctx, operation, "rdx", resultAddress);
        TraceReadableQword(ctx, operation, "rcx", ctx[CpuRegister.Rcx]);
        TraceReadableQword(ctx, operation, "r8", ctx[CpuRegister.R8]);
    }

    private static void TraceReadableQword(CpuContext ctx, string operation, string name, ulong address)
    {
        if (address == 0 || !ctx.TryReadUInt64(address, out var value))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}.{name}: addr=0x{address:X16} q0=0x{value:X16}");
    }
}
