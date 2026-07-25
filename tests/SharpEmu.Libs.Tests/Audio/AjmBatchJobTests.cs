// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// Covers the batch-job entry points a PS5 title uses to build an AJM batch: argument
/// validation, queueing against the batch object, and the sideband results the jobs publish
/// when the batch is started.
/// </summary>
[Collection(AjmStateCollection.Name)]
public sealed class AjmBatchJobTests : IDisposable
{
    private const int InvalidContext = unchecked((int)0x80930002);
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int OutOfMemory = unchecked((int)0x80930006);
    private const int InvalidAddress = unchecked((int)0x80930016);

    private const int ResultUnsupportedFlag = 0x0000_0080;
    private const int ResultFatal = unchecked((int)0x8000_0000);
    private const int ResultInvalidParameter = 0x0000_0004;

    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x10;
    private const ulong InstanceAddress = MemoryBase + 0x20;
    private const ulong BatchIdAddress = MemoryBase + 0x30;
    private const ulong BatchInfoAddress = MemoryBase + 0x100;
    private const ulong BatchBufferAddress = MemoryBase + 0x200;
    private const ulong SidebandAddress = MemoryBase + 0x1000;
    private const ulong InputAddress = MemoryBase + 0x2000;
    private const ulong OutputAddress = MemoryBase + 0x3000;
    private const ulong DescriptorAddress = MemoryBase + 0x4000;
    private const ulong StackAddress = MemoryBase + 0x8000;

    /// <summary>Well past the end of the fake region, so reads and writes fault.</summary>
    private const ulong UnmappedAddress = MemoryBase + 0x10_0000;

    private const ulong BatchBufferSize = 0x800;

    private readonly AllocatingFakeCpuMemory _memory = new(MemoryBase, 0x20000);
    private readonly CpuContext _ctx;

    public AjmBatchJobTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = StackAddress,
        };
    }

    public void Dispose() => AjmExports.ResetForTests();

    [Theory]
    [InlineData("ezM2OhNxzck", "sceAjmBatchJobInitialize")]
    [InlineData("uJ3m8INuikg", "sceAjmBatchJobClearContext")]
    [InlineData("SJ3i0DXP8vg", "sceAjmBatchJobDecodeSplit")]
    [InlineData("3cAg7xN995U", "sceAjmBatchJobGetStatistics")]
    [InlineData("JkdNCocpu1M", "sceAjmBatchJobGetResampleInfo")]
    [InlineData("5ldnD16rYZw", "sceAjmBatchJobSetResampleParametersEx")]
    [InlineData("SkEwpiu3tZg", "sceAjmBatchJobSetGaplessDecode")]
    [InlineData("AxhcqVv5AYU", "sceAjmStrError")]
    [InlineData("WfAiBW8Wcek", "sceAjmBatchErrorDump")]
    public void PreviouslyUnresolvedExports_ResolveForBothGenerations(string nid, string exportName)
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport(nid, out var export), $"{exportName} unresolved for {generation}");
            Assert.Equal(exportName, export.Name);
        }
    }

    [Fact]
    public void BatchJobs_RejectNullBatchObject()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);

        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobInitialize, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobClearContext, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobDecode, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobDecodeSplit, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobGetStatistics, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobGetResampleInfo, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobSetGaplessDecode, 0, instanceId));
        Assert.Equal(InvalidParameter, CallJob(AjmExports.AjmBatchJobSetResampleParametersEx, 0, instanceId));
    }

    [Fact]
    public void BatchStart_RejectsUnknownContextAndReportsItInTheBatchError()
    {
        var contextId = Initialize();
        WriteBatchInfo();

        var result = StartBatch(contextId + 7, errorAddress: SidebandAddress);

        Assert.Equal(InvalidContext, result);
        Assert.Equal(InvalidContext, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void BatchStart_RejectsMissingBatchOrOutputPointer()
    {
        var contextId = Initialize();
        WriteBatchInfo();

        Assert.Equal(InvalidParameter, StartBatch(contextId, infoAddress: 0));
        Assert.Equal(InvalidParameter, StartBatch(contextId, batchIdAddress: 0));
    }

    [Fact]
    public void BatchJob_ReportsOutOfMemoryWhenTheGuestBatchBufferIsFull()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);

        // Only room for a single job in the guest's batch storage.
        WriteBatchInfo(size: 64);

        Assert.Equal(0, CallJob(AjmExports.AjmBatchJobClearContext, BatchInfoAddress, instanceId));
        Assert.Equal(OutOfMemory, CallJob(AjmExports.AjmBatchJobClearContext, BatchInfoAddress, instanceId));
    }

    [Fact]
    public void BatchInitialize_DropsJobsQueuedAgainstAReusedBatchObject()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        Assert.Equal(0, CallClearContext(instanceId, SidebandAddress));

        // Re-initializing the batch must discard the queued job, so starting the batch
        // afterwards performs no work and leaves the poisoned sideband untouched.
        WriteBatchInfo();
        WriteUInt32(SidebandAddress, 0xDEADBEEF);
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));

        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(0xDEADBEEFu, ReadUInt32(SidebandAddress));
    }

    [Fact]
    public void ClearContextJob_RunsOnlyWhenTheBatchIsStarted()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        WriteUInt32(SidebandAddress, 0xDEADBEEF);
        Assert.Equal(0, CallClearContext(instanceId, SidebandAddress));

        // Queueing pre-clears the sideband but does not run the job.
        Assert.Equal(0u, ReadUInt32(SidebandAddress));

        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(0, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void JobAgainstUnknownInstance_ReportsFatalInTheSideband()
    {
        var contextId = Initialize();
        _ = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        Assert.Equal(0, CallClearContext(instanceId: 0x4FFF, SidebandAddress));
        Assert.Equal(0, StartBatch(contextId));

        Assert.Equal(ResultFatal | ResultInvalidParameter, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void InitializeJob_CapturesCodecConfigurationAndReportsSuccess()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        // Four ATRAC9 config bytes plus the reserved word.
        Write(InputAddress, [0xFE, 0x74, 0x0F, 0xF0, 0, 0, 0, 0]);

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = InputAddress;
        _ctx[CpuRegister.Rcx] = 8;
        _ctx[CpuRegister.R8] = SidebandAddress;
        _ctx[CpuRegister.R9] = 32;
        Assert.Equal(0, AjmExports.AjmBatchJobInitialize(_ctx));

        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(0, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void InitializeJob_ReportsInvalidParameterWhenTheConfigBlockIsUnreadable()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = UnmappedAddress;
        _ctx[CpuRegister.Rcx] = 8;
        _ctx[CpuRegister.R8] = SidebandAddress;
        _ctx[CpuRegister.R9] = 32;
        Assert.Equal(0, AjmExports.AjmBatchJobInitialize(_ctx));

        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(ResultInvalidParameter, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void SetGaplessDecodeJob_AcceptsAPointerParameterBlock()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        WriteUInt32(InputAddress, 44100);
        WriteUInt16(InputAddress + 4, 1024);

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = InputAddress;
        _ctx[CpuRegister.Rcx] = SidebandAddress;
        _ctx[CpuRegister.R8] = 32;
        Assert.Equal(0, AjmExports.AjmBatchJobSetGaplessDecode(_ctx));

        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(0, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void SetResampleParametersEx_ReportsUnsupportedForANonUnityRatio()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        WriteUInt32(InputAddress, (uint)BitConverter.SingleToInt32Bits(2.0f));
        WriteUInt32(InputAddress + 4, 0);

        Assert.Equal(0, CallSetResample(instanceId, InputAddress));
        Assert.Equal(0, StartBatch(contextId));

        Assert.Equal(ResultUnsupportedFlag, ReadInt32(SidebandAddress));
    }

    [Fact]
    public void SetResampleParametersEx_AcceptsUnityRatioAndGetResampleInfoReportsItBack()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        WriteUInt32(InputAddress, (uint)BitConverter.SingleToInt32Bits(1.0f));
        WriteUInt32(InputAddress + 4, 0x5);

        Assert.Equal(0, CallSetResample(instanceId, InputAddress));
        Assert.Equal(0, StartBatch(contextId));
        Assert.Equal(0, ReadInt32(SidebandAddress));

        WriteBatchInfo();
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = SidebandAddress;
        _ctx[CpuRegister.Rcx] = 32;
        Assert.Equal(0, AjmExports.AjmBatchJobGetResampleInfo(_ctx));
        Assert.Equal(0, StartBatch(contextId));

        Assert.Equal(0, ReadInt32(SidebandAddress));
        Assert.Equal(1.0f, BitConverter.Int32BitsToSingle(ReadInt32(SidebandAddress + 8)));
        Assert.Equal(0x5u, ReadUInt32(SidebandAddress + 12));
    }

    [Fact]
    public void GetStatisticsJob_PublishesMemoryCountersWithoutAnInstance()
    {
        var contextId = Initialize();
        WriteBatchInfo();

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = 0x1_0001;
        _ctx[CpuRegister.Rdx] = SidebandAddress;
        _ctx[CpuRegister.Rcx] = 48;
        Assert.Equal(0, AjmExports.AjmBatchJobGetStatistics(_ctx));
        Assert.Equal(0, StartBatch(contextId));

        Assert.Equal(0, ReadInt32(SidebandAddress));
        // AjmSidebandStatisticsMemory.instance_free follows the result and engine blocks.
        Assert.NotEqual(0u, ReadUInt32(SidebandAddress + 24));
    }

    [Fact]
    public void DecodeSplit_RejectsUnreadableDescriptorArrays()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = UnmappedAddress;
        _ctx[CpuRegister.Rcx] = 2;
        _ctx[CpuRegister.R8] = 0;
        _ctx[CpuRegister.R9] = 0;
        Assert.Equal(InvalidAddress, AjmExports.AjmBatchJobDecodeSplit(_ctx));
    }

    [Fact]
    public void DecodeSplit_RejectsAnImplausibleDescriptorCount()
    {
        var contextId = Initialize();
        var instanceId = CreateInstance(contextId, codecType: 1);
        WriteBatchInfo();

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = DescriptorAddress;
        _ctx[CpuRegister.Rcx] = 4096;
        _ctx[CpuRegister.R8] = 0;
        _ctx[CpuRegister.R9] = 0;
        Assert.Equal(InvalidParameter, AjmExports.AjmBatchJobDecodeSplit(_ctx));
    }

    [Fact]
    public void DecodeWithoutADecoder_EmitsSilenceAndConsumesTheInput()
    {
        var contextId = Initialize();
        // Codec 7 has no SharpEmu decoder, which is the documented logged-silence path.
        var instanceId = CreateInstance(contextId, codecType: 7);
        WriteBatchInfo();

        Write(InputAddress, [1, 2, 3, 4, 5, 6, 7, 8]);
        for (var offset = 0ul; offset < 64; offset += 4)
        {
            WriteUInt32(OutputAddress + offset, 0x7F7F7F7F);
        }

        WriteStackArg(0, SidebandAddress);
        WriteStackArg(1, 32);
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = InputAddress;
        _ctx[CpuRegister.Rcx] = 8;
        _ctx[CpuRegister.R8] = OutputAddress;
        _ctx[CpuRegister.R9] = 64;
        Assert.Equal(0, AjmExports.AjmBatchJobDecode(_ctx));
        Assert.Equal(0, StartBatch(contextId));

        Assert.Equal(0, ReadInt32(SidebandAddress));
        Assert.Equal(8, ReadInt32(SidebandAddress + 8));
        Assert.Equal(64, ReadInt32(SidebandAddress + 12));
        for (var offset = 0ul; offset < 64; offset += 4)
        {
            Assert.Equal(0u, ReadUInt32(OutputAddress + offset));
        }
    }

    [Fact]
    public void StrError_PublishesAGuestReadableDescriptionPerErrorCode()
    {
        var contextId = Initialize();
        Assert.NotEqual(0u, contextId);

        Assert.Equal("SCE_AJM_ERROR_INVALID_CONTEXT", StrError(InvalidContext));
        Assert.Equal("SCE_AJM_ERROR_INVALID_PARAMETER", StrError(InvalidParameter));
        Assert.Equal("SCE_AJM_ERROR_CANCELLED", StrError(unchecked((int)0x80930017)));
        Assert.Equal("SCE_OK", StrError(0));

        // Codes outside the AJM range fall back to the catch-all entry rather than
        // indexing off the end of the table.
        Assert.Equal("SCE_AJM_ERROR_UNKNOWN", StrError(unchecked((int)0x80930099)));
        Assert.Equal("SCE_AJM_ERROR_UNKNOWN", StrError(0x1234));
    }

    [Fact]
    public void StrError_ReturnsNullWhenGuestMemoryCannotBeAllocated()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)(long)InvalidContext);

        Assert.Equal(OutOfMemory, AjmExports.AjmStrError(context));
        Assert.Equal(0ul, context[CpuRegister.Rax]);
    }

    [Fact]
    public void BatchErrorDump_ToleratesAnUnreadablePointer()
    {
        _ctx[CpuRegister.Rdi] = UnmappedAddress;
        Assert.Equal(0, AjmExports.AjmBatchErrorDump(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, AjmExports.AjmBatchErrorDump(_ctx));
    }

    [Fact]
    public void BatchErrorDump_ReadsAPopulatedBatchError()
    {
        WriteUInt32(SidebandAddress, unchecked((uint)InvalidContext));
        WriteUInt64(SidebandAddress + 8, 0x1234_5678);
        WriteUInt32(SidebandAddress + 16, 0x40);

        _ctx[CpuRegister.Rdi] = SidebandAddress;
        Assert.Equal(0, AjmExports.AjmBatchErrorDump(_ctx));
    }

    [Theory]
    [InlineData(0, "SCE_OK")]
    [InlineData(unchecked((int)0x80930001), "SCE_AJM_ERROR_UNKNOWN")]
    [InlineData(unchecked((int)0x80930004), "SCE_AJM_ERROR_INVALID_BATCH")]
    [InlineData(unchecked((int)0x80930011), "SCE_AJM_ERROR_MALFORMED_BATCH")]
    [InlineData(unchecked((int)0x80930017), "SCE_AJM_ERROR_CANCELLED")]
    [InlineData(unchecked((int)0x80930018), "SCE_AJM_ERROR_UNKNOWN")]
    public void DescribeError_MapsEveryDocumentedCode(int errorCode, string expected) =>
        Assert.Equal(expected, AjmExports.DescribeError(errorCode));

    private uint Initialize()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        return ReadUInt32(ContextAddress);
    }

    private uint CreateInstance(uint contextId, uint codecType)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AjmExports.AjmModuleRegister(_ctx));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        // version=1, channels=2, format=S16.
        _ctx[CpuRegister.Rdx] = 1 | (2 << 3);
        _ctx[CpuRegister.Rcx] = InstanceAddress;
        Assert.Equal(0, AjmExports.AjmInstanceCreate(_ctx));
        return ReadUInt32(InstanceAddress);
    }

    private void WriteBatchInfo(ulong size = BatchBufferSize)
    {
        WriteUInt64(BatchInfoAddress, BatchBufferAddress);
        WriteUInt64(BatchInfoAddress + 8, 0);
        WriteUInt64(BatchInfoAddress + 16, size);
        WriteUInt64(BatchInfoAddress + 24, 0);
    }

    private int StartBatch(
        uint contextId,
        ulong infoAddress = BatchInfoAddress,
        ulong batchIdAddress = BatchIdAddress,
        ulong errorAddress = 0)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = infoAddress;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = errorAddress;
        _ctx[CpuRegister.R8] = batchIdAddress;
        return AjmExports.AjmBatchStart(_ctx);
    }

    private int CallJob(Func<CpuContext, int> job, ulong infoAddress, uint instanceId)
    {
        _ctx[CpuRegister.Rdi] = infoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;
        _ctx[CpuRegister.R8] = 0;
        _ctx[CpuRegister.R9] = 0;
        return job(_ctx);
    }

    private int CallClearContext(uint instanceId, ulong sidebandAddress)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = sidebandAddress;
        _ctx[CpuRegister.Rcx] = 32;
        return AjmExports.AjmBatchJobClearContext(_ctx);
    }

    private int CallSetResample(uint instanceId, ulong parametersAddress)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = parametersAddress;
        _ctx[CpuRegister.Rcx] = SidebandAddress;
        _ctx[CpuRegister.R8] = 32;
        return AjmExports.AjmBatchJobSetResampleParametersEx(_ctx);
    }

    private string StrError(int errorCode)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)(long)errorCode);
        Assert.Equal(0, AjmExports.AjmStrError(_ctx));
        var address = _ctx[CpuRegister.Rax];
        Assert.NotEqual(0ul, address);

        var bytes = new byte[48];
        Assert.True(_memory.TryRead(address, bytes));
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
    }

    private void WriteStackArg(int index, ulong value) =>
        WriteUInt64(StackAddress + 8 + ((ulong)index * 8), value);

    private void Write(ulong address, byte[] bytes) => Assert.True(_memory.TryWrite(address, bytes));

    private void WriteUInt16(ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private int ReadInt32(ulong address) => unchecked((int)ReadUInt32(address));
}
