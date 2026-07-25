// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int InvalidParameter = unchecked((int)0x806A0001);
    private const uint MaximumGen5InitializeRevision = 3;
    private const int OrbisAjmErrorUnknown = unchecked((int)0x80930001);
    private const int OrbisAjmErrorInvalidContext = unchecked((int)0x80930002);
    private const int OrbisAjmErrorInvalidInstance = unchecked((int)0x80930003);
    private const int OrbisAjmErrorInvalidBatch = unchecked((int)0x80930004);
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private const int OrbisAjmErrorOutOfMemory = unchecked((int)0x80930006);
    private const int OrbisAjmErrorOutOfResources = unchecked((int)0x80930007);
    private const int OrbisAjmErrorCodecNotSupported = unchecked((int)0x80930008);
    private const int OrbisAjmErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int OrbisAjmErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int OrbisAjmErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const int OrbisAjmErrorFlagNotSupported = unchecked((int)0x8093000C);
    private const int OrbisAjmErrorBusy = unchecked((int)0x8093000D);
    private const int OrbisAjmErrorBadPriority = unchecked((int)0x8093000E);
    private const int OrbisAjmErrorInProgress = unchecked((int)0x8093000F);
    private const int OrbisAjmErrorRetry = unchecked((int)0x80930010);
    private const int OrbisAjmErrorMalformedBatch = unchecked((int)0x80930011);
    private const int OrbisAjmErrorJobCreation = unchecked((int)0x80930012);
    private const int OrbisAjmErrorInvalidOpcode = unchecked((int)0x80930013);
    private const int OrbisAjmErrorPriorityViolation = unchecked((int)0x80930014);
    private const int OrbisAjmErrorBufferTooBig = unchecked((int)0x80930015);
    private const int OrbisAjmErrorInvalidAddress = unchecked((int)0x80930016);
    private const int OrbisAjmErrorCancelled = unchecked((int)0x80930017);

    private const uint MaxCodecType = 25;
    private const int MaxInstanceIndex = 0x2FFF;

    /// <summary>Pseudo instance id reserved for engine/memory statistics jobs.</summary>
    private const uint StatisticsInstanceId = 0x80000;

    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();

    /// <summary>
    /// Queued jobs per <c>SceAjmBatchInfo</c> guest address. AJM batches are opaque to the
    /// guest — it only ever hands the batch object back to libSceAjm — so the job list lives
    /// on the host instead of being serialized into the guest's batch buffer. That keeps us
    /// from having to guess Sony's private chunk encoding or how large the guest sized its
    /// batch storage.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, AjmBatchState> Batches = new();

    private static int _nextContextId;
    private static int _nextBatchId;
    private static ulong _errorStringTableAddress;

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, AjmInstanceState> InstancesBySlot { get; } = new();

        public int NextInstanceIndex { get; set; }
    }

    /// <summary>
    /// Per-instance decode state. <c>sceAjmInstanceCreate</c> is the only place the guest
    /// declares the codec, channel count and PCM encoding, so it is captured here and reused
    /// by every decode job routed to the instance.
    /// </summary>
    private sealed class AjmInstanceState(uint instanceId, uint codecType, ulong flags)
    {
        public uint InstanceId { get; } = instanceId;

        public uint CodecType { get; } = codecType;

        public ulong Flags { get; } = flags;

        /// <summary>AjmInstanceFlags.channels — bits 3..6.</summary>
        public int Channels { get; } = (int)((flags >> 3) & 0xF);

        /// <summary>AjmInstanceFlags.format — bits 7..9.</summary>
        public AjmFormatEncoding Encoding { get; } =
            (AjmFormatEncoding)((flags >> 7) & 0x7) is var encoding &&
            encoding is AjmFormatEncoding.S16 or AjmFormatEncoding.S32 or AjmFormatEncoding.Float
                ? encoding
                : AjmFormatEncoding.S16;

        /// <summary>AjmInstanceFlags.gapless_loop — bit 10.</summary>
        public bool GaplessLoop { get; } = ((flags >> 10) & 1) != 0;

        public AjmDecoderSession? Decoder { get; set; }

        /// <summary>Codec configuration captured from the most recent initialize job.</summary>
        public byte[]? ConfigData { get; set; }

        public ulong TotalDecodedSamples { get; set; }

        public uint GaplessTotalSamples { get; set; }

        public ushort GaplessSkipSamples { get; set; }

        public ushort GaplessSkippedSamples { get; set; }

        public float ResampleRatio { get; set; } = 1.0f;

        public uint ResampleFlags { get; set; }

        public void Reset()
        {
            TotalDecodedSamples = 0;
            GaplessSkippedSamples = 0;
            Decoder?.Reset();
        }

        public void Dispose()
        {
            Decoder?.Dispose();
            Decoder = null;
        }
    }

    private sealed class AjmBatchState
    {
        public object Gate { get; } = new();

        public List<AjmJob> Jobs { get; } = new();
    }

    public static int AjmInitialize(CpuContext ctx)
    {
        var initializeFlags = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!IsValidInitializeFlags(ctx.TargetGeneration, initializeFlags) || outputAddress == 0)
        {
            return InvalidParameter;
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return InvalidParameter;
        }

        Contexts[contextId] = new AjmContextState();
        Trace(
            $"initialize flags=0x{initializeFlags:X16} revision={initializeFlags >> 32} " +
            $"out=0x{outputAddress:X16} context={contextId}");

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    internal static bool IsValidInitializeFlags(Generation generation, ulong initializeFlags)
    {
        // Gen4 defines this argument as an s64 reserved value and requires
        // zero. Gen5 retained the same NID and output pointer but encodes the
        // AJM ABI revision in the high dword. Current SDK middleware passes
        // 0x00000003_00000000; the low reserved dword must remain zero.
        if (initializeFlags == 0)
        {
            return true;
        }

        var revision = initializeFlags >> 32;
        return generation == Generation.Gen5 &&
               (initializeFlags & uint.MaxValue) == 0 &&
               revision is >= 1 and <= MaximumGen5InitializeRevision;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        if (Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out var state))
        {
            lock (state.Gate)
            {
                foreach (var instance in state.InstancesBySlot.Values)
                {
                    instance.Dispose();
                }

                state.InstancesBySlot.Clear();
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (codecType >= MaxCodecType || reserved != 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Add(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecAlreadyRegistered);
            }
        }

        Trace($"module_register context={contextId} codec={codecType} ({AjmCodecType.Name(codecType)})");
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceCreate(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorWrongRevisionFlag);
        }

        uint instanceId;
        AjmInstanceState instance;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << 14) | instanceSlot;
            Span<byte> value = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
            if (!ctx.Memory.TryWrite(outputAddress, value))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            state.NextInstanceIndex = nextInstanceIndex;
            instance = new AjmInstanceState(instanceId, codecType, flags);
            state.InstancesBySlot.Add(instanceSlot, instance);
        }

        Trace(
            $"instance_create context={contextId} codec={codecType} ({AjmCodecType.Name(codecType)}) " +
            $"flags=0x{flags:X} channels={instance.Channels} encoding={instance.Encoding} " +
            $"gapless_loop={instance.GaplessLoop} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        var instanceSlot = instanceId & 0x3FFF;
        AjmInstanceState? instance;
        lock (state.Gate)
        {
            if (instanceSlot == 0 || !state.InstancesBySlot.Remove(instanceSlot, out instance))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidInstance);
            }
        }

        instance.Dispose();
        Trace($"instance_destroy context={contextId} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Remove(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }
        }

        Trace($"module_unregister context={contextId} codec={codecType}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // The caller owns and initializes the batch storage. This API resets the
        // submission cursor; drop any jobs still queued against the batch object so a
        // reused SceAjmBatchInfo cannot replay the previous batch.
        var infoAddress = ctx[CpuRegister.Rdi];
        var alternateAddress = ctx[CpuRegister.Rdx];
        ClearBatch(infoAddress);

        // Some SDK revisions spell this as (buffer, size, pBatchInfo). Clearing the third
        // argument's queue too costs nothing and keeps either ordering correct.
        if (alternateAddress != 0 && alternateAddress != infoAddress)
        {
            ClearBatch(alternateAddress);
        }

        if (infoAddress != 0)
        {
            _ = TryWriteUInt64(ctx, infoAddress + AjmBatchInfoOffsetField, 0);
        }

        Trace($"batch_initialize info=0x{infoAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    /// <summary>
    /// Queues a decode job on a batch. The job is recorded, not executed: AJM only runs a
    /// batch when the guest submits it with <c>sceAjmBatchStart</c>.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId, const void* pInput,
    /// size_t inputSize, void* pOutput, size_t outputSize, void* pSideband,
    /// size_t sidebandSize)</c>. Enable <c>SHARPEMU_LOG_AJM=1</c> to dump the raw argument
    /// registers if a title behaves as though the ordering differs.
    /// </remarks>
    [SysAbiExport(
        Nid = "39WxhR-ePew",
        ExportName = "sceAjmBatchJobDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobDecode(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputAddress = ctx[CpuRegister.Rdx];
        var inputSize = ctx[CpuRegister.Rcx];
        var outputAddress = ctx[CpuRegister.R8];
        var outputSize = ctx[CpuRegister.R9];
        var sidebandAddress = ReadStackArg64(ctx, 0);
        var sidebandSize = ResolveSidebandSize(ReadStackArg64(ctx, 1));

        TraceArguments(ctx, "batch_job_decode");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.Decode,
            InstanceId = instanceId,
            InputBuffers = inputAddress != 0 && inputSize != 0
                ? [new AjmGuestBuffer(inputAddress, inputSize)]
                : [],
            OutputBuffers = outputAddress != 0 && outputSize != 0
                ? [new AjmGuestBuffer(outputAddress, outputSize)]
                : [],
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues a decode job whose compressed input and PCM output are described by arrays of
    /// <c>SceAjmBuffer</c> descriptors rather than a single range. Used when a title's ring
    /// buffer wraps mid-frame.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId, const SceAjmBuffer* pIn,
    /// size_t numIn, const SceAjmBuffer* pOut, size_t numOut, void* pSideband,
    /// size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "SJ3i0DXP8vg",
        ExportName = "sceAjmBatchJobDecodeSplit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobDecodeSplit(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputDescriptors = ctx[CpuRegister.Rdx];
        var inputCount = ctx[CpuRegister.Rcx];
        var outputDescriptors = ctx[CpuRegister.R8];
        var outputCount = ctx[CpuRegister.R9];
        var sidebandAddress = ReadStackArg64(ctx, 0);
        var sidebandSize = ResolveSidebandSize(ReadStackArg64(ctx, 1));

        TraceArguments(ctx, "batch_job_decode_split");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (inputCount > MaxSplitBuffers || outputCount > MaxSplitBuffers)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!TryReadBufferDescriptors(ctx, inputDescriptors, inputCount, out var inputBuffers) ||
            !TryReadBufferDescriptors(ctx, outputDescriptors, outputCount, out var outputBuffers))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.DecodeSplit,
            InstanceId = instanceId,
            InputBuffers = inputBuffers,
            OutputBuffers = outputBuffers,
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues the codec-initialize control job. The sideband input carries the codec's
    /// configuration blob — for ATRAC9 the four <c>SceAjmDecAt9InitializeParameters</c>
    /// config bytes lifted from the stream's RIFF header.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId, const void* pInitParams,
    /// size_t initParamsSize, void* pSideband, size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "ezM2OhNxzck",
        ExportName = "sceAjmBatchJobInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobInitialize(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var initParamsAddress = ctx[CpuRegister.Rdx];
        var initParamsSize = ctx[CpuRegister.Rcx];
        var sidebandAddress = ctx[CpuRegister.R8];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.R9]);

        TraceArguments(ctx, "batch_job_initialize");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        // The initialize sideband is a small fixed-size union (8 bytes today). Clamp
        // rather than reject so a larger future revision still initializes the codec.
        if (initParamsSize is 0 or > MaxSidebandInputBytes)
        {
            initParamsSize = Math.Min(Math.Max(initParamsSize, InitializeParametersBytes), MaxSidebandInputBytes);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.Initialize,
            InstanceId = instanceId,
            SidebandInput = new AjmGuestBuffer(initParamsAddress, initParamsSize),
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues the reset control job. This is the "rewind this instance" path titles use when
    /// a voice loops or is re-pooled, so it must drop decoder state rather than succeed
    /// silently — otherwise the next stream decodes against the previous stream's history.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId, void* pSideband,
    /// size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "uJ3m8INuikg",
        ExportName = "sceAjmBatchJobClearContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobClearContext(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var sidebandAddress = ctx[CpuRegister.Rdx];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.Rcx]);

        TraceArguments(ctx, "batch_job_clear_context");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.ClearContext,
            InstanceId = instanceId,
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues the gapless-decode control job, which tells the codec how many samples the
    /// stream really contains and how many encoder-delay samples to drop up front.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId,
    /// const SceAjmSidebandGaplessDecode* pParams, void* pSideband, size_t sidebandSize)</c>.
    /// The parameter block is only eight bytes, so a title that passes it by value in RDX
    /// is also accepted.
    /// </remarks>
    [SysAbiExport(
        Nid = "SkEwpiu3tZg",
        ExportName = "sceAjmBatchJobSetGaplessDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobSetGaplessDecode(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var parametersAddress = ctx[CpuRegister.Rdx];
        var sidebandAddress = ctx[CpuRegister.Rcx];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.R8]);

        TraceArguments(ctx, "batch_job_set_gapless_decode");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.SetGaplessDecode,
            InstanceId = instanceId,
            Flags = parametersAddress,
            SidebandInput = new AjmGuestBuffer(parametersAddress, GaplessDecodeBytes),
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues the extended resample control job. SharpEmu records the ratio but does not
    /// resample, so execution reports <c>ORBIS_AJM_RESULT_UNSUPPORTED_FLAG</c> for any ratio
    /// other than 1:1 instead of pretending the request was honoured.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId,
    /// const SceAjmSidebandResampleParameters* pParams, void* pSideband,
    /// size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "5ldnD16rYZw",
        ExportName = "sceAjmBatchJobSetResampleParametersEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobSetResampleParametersEx(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var parametersAddress = ctx[CpuRegister.Rdx];
        var sidebandAddress = ctx[CpuRegister.Rcx];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.R8]);

        TraceArguments(ctx, "batch_job_set_resample_parameters_ex");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.SetResampleParameters,
            InstanceId = instanceId,
            SidebandInput = new AjmGuestBuffer(parametersAddress, ResampleParametersBytes),
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues a job that reports the instance's current resample settings back through the
    /// sideband output.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, SceAjmInstanceId, void* pSideband,
    /// size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "JkdNCocpu1M",
        ExportName = "sceAjmBatchJobGetResampleInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobGetResampleInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var sidebandAddress = ctx[CpuRegister.Rdx];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.Rcx]);

        TraceArguments(ctx, "batch_job_get_resample_info");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.GetResampleInfo,
            InstanceId = instanceId,
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Queues an engine-statistics job. Statistics jobs are not bound to a decoder instance;
    /// they use the reserved statistics instance id and report engine load and free-memory
    /// counters.
    /// </summary>
    /// <remarks>
    /// Inferred signature: <c>(SceAjmBatchInfo*, uint64_t statisticsFlags, void* pSideband,
    /// size_t sidebandSize)</c>.
    /// </remarks>
    [SysAbiExport(
        Nid = "3cAg7xN995U",
        ExportName = "sceAjmBatchJobGetStatistics",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobGetStatistics(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var statisticsFlags = ctx[CpuRegister.Rsi];
        var sidebandAddress = ctx[CpuRegister.Rdx];
        var sidebandSize = ResolveSidebandSize(ctx[CpuRegister.Rcx], StatisticsSidebandBytes);

        TraceArguments(ctx, "batch_job_get_statistics");
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var job = new AjmJob
        {
            Kind = AjmJobKind.GetStatistics,
            InstanceId = StatisticsInstanceId,
            Flags = statisticsFlags,
            SidebandOutput = new AjmGuestBuffer(sidebandAddress, sidebandSize),
        };

        return ctx.SetReturn(AppendJob(ctx, infoAddress, job));
    }

    /// <summary>
    /// Submits a built batch. SharpEmu has no asynchronous audio co-processor, so the batch
    /// runs to completion here and <c>sceAjmBatchWait</c> becomes a no-op.
    /// </summary>
    [SysAbiExport(
        Nid = "5tOfnaClcqM",
        ExportName = "sceAjmBatchStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchStart(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        var priority = unchecked((int)ctx[CpuRegister.Rdx]);
        var errorAddress = ctx[CpuRegister.Rcx];
        var batchOutAddress = ctx[CpuRegister.R8];

        if (infoAddress == 0 || batchOutAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var contextState))
        {
            WriteAjmBatchError(ctx, errorAddress, OrbisAjmErrorInvalidContext, 0);
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        ClearAjmBatchError(ctx, errorAddress);

        var jobs = TakeJobs(infoAddress);
        var executed = 0;
        foreach (var job in jobs)
        {
            ExecuteJob(ctx, contextState, job);
            executed++;
        }

        var batchId = unchecked((uint)Interlocked.Increment(ref _nextBatchId));
        Span<byte> batchValue = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(batchValue, batchId);
        if (!ctx.Memory.TryWrite(batchOutAddress, batchValue))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Trace(
            $"batch_start context={contextId} info=0x{infoAddress:X16} " +
            $"priority={priority} batch={batchId} jobs={executed}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-qLsfDAywIY",
        ExportName = "sceAjmBatchWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchWait(CpuContext ctx)
    {
        // Batches complete synchronously in Start; Wait is a no-op success.
        var errorAddress = ctx[CpuRegister.Rcx];
        ClearAjmBatchError(ctx, errorAddress);
        Trace(
            $"batch_wait context={unchecked((uint)ctx[CpuRegister.Rdi])} " +
            $"batch={unchecked((uint)ctx[CpuRegister.Rsi])} " +
            $"timeout={unchecked((uint)ctx[CpuRegister.Rdx])}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "NVDXiUesSbA",
        ExportName = "sceAjmBatchCancel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchCancel(CpuContext ctx)
    {
        Trace(
            $"batch_cancel context={unchecked((uint)ctx[CpuRegister.Rdi])} " +
            $"batch={unchecked((uint)ctx[CpuRegister.Rsi])}");
        return ctx.SetReturn(0);
    }

    /// <summary>
    /// Maps an AJM error code to a static description string. Returns a guest pointer in RAX,
    /// or NULL when no guest memory can be allocated for the table.
    /// </summary>
    [SysAbiExport(
        Nid = "AxhcqVv5AYU",
        ExportName = "sceAjmStrError",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmStrError(CpuContext ctx)
    {
        var errorCode = unchecked((int)ctx[CpuRegister.Rdi]);
        var address = GetErrorStringAddress(ctx, errorCode);
        ctx[CpuRegister.Rax] = address;
        Trace($"str_error code=0x{errorCode:X8} text=\"{DescribeError(errorCode)}\" address=0x{address:X16}");

        // The export returns a pointer, so the int return value only reports whether a
        // string could be published; RAX carries the real result.
        return address != 0 ? 0 : OrbisAjmErrorOutOfMemory;
    }

    /// <summary>
    /// Dumps a <c>SceAjmBatchError</c> to the emulator log. This is a diagnostic helper, so a
    /// pointer that cannot be read is reported in the log rather than turned into a failure
    /// the title has to handle.
    /// </summary>
    [SysAbiExport(
        Nid = "WfAiBW8Wcek",
        ExportName = "sceAjmBatchErrorDump",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchErrorDump(CpuContext ctx)
    {
        var errorAddress = ctx[CpuRegister.Rdi];
        Span<byte> error = stackalloc byte[AjmBatchErrorBytes];
        if (errorAddress != 0 && ctx.Memory.TryRead(errorAddress, error))
        {
            var errorCode = BinaryPrimitives.ReadInt32LittleEndian(error);
            var jobAddress = BinaryPrimitives.ReadUInt64LittleEndian(error[8..]);
            var commandOffset = BinaryPrimitives.ReadUInt32LittleEndian(error[16..]);
            Console.Error.WriteLine(
                $"[LOADER][WARN] ajm.batch_error code=0x{errorCode:X8} ({DescribeError(errorCode)}) " +
                $"job=0x{jobAddress:X16} cmd_offset=0x{commandOffset:X}");
        }
        else
        {
            Trace($"batch_error_dump unreadable error=0x{errorAddress:X16}");
        }

        return ctx.SetReturn(0);
    }

    internal static string DescribeError(int errorCode) => errorCode switch
    {
        0 => "SCE_OK",
        OrbisAjmErrorUnknown => "SCE_AJM_ERROR_UNKNOWN",
        OrbisAjmErrorInvalidContext => "SCE_AJM_ERROR_INVALID_CONTEXT",
        OrbisAjmErrorInvalidInstance => "SCE_AJM_ERROR_INVALID_INSTANCE",
        OrbisAjmErrorInvalidBatch => "SCE_AJM_ERROR_INVALID_BATCH",
        OrbisAjmErrorInvalidParameter => "SCE_AJM_ERROR_INVALID_PARAMETER",
        OrbisAjmErrorOutOfMemory => "SCE_AJM_ERROR_OUT_OF_MEMORY",
        OrbisAjmErrorOutOfResources => "SCE_AJM_ERROR_OUT_OF_RESOURCES",
        OrbisAjmErrorCodecNotSupported => "SCE_AJM_ERROR_CODEC_NOT_SUPPORTED",
        OrbisAjmErrorCodecAlreadyRegistered => "SCE_AJM_ERROR_CODEC_ALREADY_REGISTERED",
        OrbisAjmErrorCodecNotRegistered => "SCE_AJM_ERROR_CODEC_NOT_REGISTERED",
        OrbisAjmErrorWrongRevisionFlag => "SCE_AJM_ERROR_WRONG_REVISION_FLAG",
        OrbisAjmErrorFlagNotSupported => "SCE_AJM_ERROR_FLAG_NOT_SUPPORTED",
        OrbisAjmErrorBusy => "SCE_AJM_ERROR_BUSY",
        OrbisAjmErrorBadPriority => "SCE_AJM_ERROR_BAD_PRIORITY",
        OrbisAjmErrorInProgress => "SCE_AJM_ERROR_IN_PROGRESS",
        OrbisAjmErrorRetry => "SCE_AJM_ERROR_RETRY",
        OrbisAjmErrorMalformedBatch => "SCE_AJM_ERROR_MALFORMED_BATCH",
        OrbisAjmErrorJobCreation => "SCE_AJM_ERROR_JOB_CREATION",
        OrbisAjmErrorInvalidOpcode => "SCE_AJM_ERROR_INVALID_OPCODE",
        OrbisAjmErrorPriorityViolation => "SCE_AJM_ERROR_PRIORITY_VIOLATION",
        OrbisAjmErrorBufferTooBig => "SCE_AJM_ERROR_BUFFER_TOO_BIG",
        OrbisAjmErrorInvalidAddress => "SCE_AJM_ERROR_INVALID_ADDRESS",
        OrbisAjmErrorCancelled => "SCE_AJM_ERROR_CANCELLED",
        _ => "SCE_AJM_ERROR_UNKNOWN",
    };

    internal static void ResetForTests()
    {
        foreach (var context in Contexts.Values)
        {
            lock (context.Gate)
            {
                foreach (var instance in context.InstancesBySlot.Values)
                {
                    instance.Dispose();
                }
            }
        }

        Contexts.Clear();
        Batches.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextBatchId, 0);
        Interlocked.Exchange(ref _errorStringTableAddress, 0);
    }

    // AjmBatchInfo: buffer, offset, size (3× u64) followed by SharpEmu bookkeeping fields.
    private const ulong AjmBatchInfoOffsetField = 8;
    private const ulong AjmBatchInfoSizeField = 16;
    private const ulong AjmBatchInfoLastGoodJobField = 24;

    /// <summary>
    /// Nominal batch-buffer cost charged per queued job. SharpEmu keeps the job list on the
    /// host, but the guest's cursor still has to advance so its "is there room for another
    /// job?" checks behave, and so it notices when its own batch storage is exhausted.
    /// </summary>
    private const ulong AjmJobRunSize = 64;
    private const ulong MaxSilentPcmBytes = 1 << 20;
    private const ulong MaxSplitBuffers = 16;
    private const ulong MaxSidebandInputBytes = 64;
    private const ulong InitializeParametersBytes = 8;
    private const ulong GaplessDecodeBytes = 8;
    private const ulong ResampleParametersBytes = 8;
    private const int BufferDescriptorBytes = 16;

    // AjmSidebandResult (8) + AjmSidebandStream (16) + AjmSidebandMFrame (8).
    private const int DecodeSidebandBytes = 32;
    private const ulong DefaultSidebandBytes = 32;
    private const ulong StatisticsSidebandBytes = 48;
    private const ulong MaxSidebandOutputBytes = 256;

    // AjmBatchError: int error_code; const void* job_addr; uint32_t cmd_offset; const void* job_ra;
    private const int AjmBatchErrorBytes = 24;

    private static AjmBatchState GetBatch(ulong infoAddress) =>
        Batches.GetOrAdd(infoAddress, static _ => new AjmBatchState());

    private static void ClearBatch(ulong infoAddress)
    {
        if (infoAddress == 0 || !Batches.TryGetValue(infoAddress, out var batch))
        {
            return;
        }

        lock (batch.Gate)
        {
            batch.Jobs.Clear();
        }
    }

    private static List<AjmJob> TakeJobs(ulong infoAddress)
    {
        if (!Batches.TryGetValue(infoAddress, out var batch))
        {
            return [];
        }

        lock (batch.Gate)
        {
            var jobs = new List<AjmJob>(batch.Jobs);
            batch.Jobs.Clear();
            return jobs;
        }
    }

    /// <summary>
    /// Records a job against a batch and advances the guest's batch cursor. The sideband
    /// output is pre-cleared to a success result so a title that never submits the batch —
    /// or submits it against a different batch object — still reads a coherent result block
    /// instead of uninitialized stack.
    /// </summary>
    private static int AppendJob(CpuContext ctx, ulong infoAddress, AjmJob job)
    {
        if (!TryAdvanceBatchCursor(ctx, infoAddress))
        {
            return OrbisAjmErrorOutOfMemory;
        }

        ClearSideband(ctx, job.SidebandOutput);

        var batch = GetBatch(infoAddress);
        lock (batch.Gate)
        {
            batch.Jobs.Add(job);
        }

        return 0;
    }

    /// <summary>
    /// Charges one job against the guest's batch storage. A guest that under-sized its batch
    /// buffer gets an honest out-of-memory failure. When the batch info cannot be read at all
    /// the cursor is left alone — some SDK revisions leave the struct opaque — and the job is
    /// still accepted.
    /// </summary>
    private static bool TryAdvanceBatchCursor(CpuContext ctx, ulong infoAddress)
    {
        if (!TryReadUInt64(ctx, infoAddress, out var buffer) ||
            !TryReadUInt64(ctx, infoAddress + AjmBatchInfoOffsetField, out var offset) ||
            !TryReadUInt64(ctx, infoAddress + AjmBatchInfoSizeField, out var size))
        {
            return true;
        }

        if (buffer == 0 || size == 0)
        {
            return true;
        }

        if (offset > size || size - offset < AjmJobRunSize)
        {
            Trace($"batch_full info=0x{infoAddress:X16} offset={offset} size={size}");
            return false;
        }

        var jobAddress = buffer + offset;
        return TryWriteUInt64(ctx, infoAddress + AjmBatchInfoLastGoodJobField, jobAddress) &&
               TryWriteUInt64(ctx, infoAddress + AjmBatchInfoOffsetField, offset + AjmJobRunSize);
    }

    private static bool TryReadBufferDescriptors(
        CpuContext ctx,
        ulong descriptorAddress,
        ulong count,
        out List<AjmGuestBuffer> buffers)
    {
        buffers = [];
        if (count == 0)
        {
            return true;
        }

        if (descriptorAddress == 0)
        {
            return false;
        }

        Span<byte> descriptor = stackalloc byte[BufferDescriptorBytes];
        for (ulong index = 0; index < count; index++)
        {
            var address = descriptorAddress + (index * BufferDescriptorBytes);
            if (!ctx.Memory.TryRead(address, descriptor))
            {
                return false;
            }

            var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
            var bufferSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]);
            if (bufferAddress != 0 && bufferSize != 0)
            {
                buffers.Add(new AjmGuestBuffer(bufferAddress, bufferSize));
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a caller-supplied sideband size. Titles that do not pass a size — or whose
    /// argument lands somewhere unexpected — fall back to the standard block so the result
    /// word is still written.
    /// </summary>
    private static ulong ResolveSidebandSize(ulong size, ulong fallback = DefaultSidebandBytes) =>
        size is >= 8 and <= MaxSidebandOutputBytes ? size : fallback;

    private static void ClearSideband(CpuContext ctx, AjmGuestBuffer sideband)
    {
        if (sideband.Address == 0 || sideband.Size == 0)
        {
            return;
        }

        ClearGuestMemory(ctx, sideband.Address, Math.Min(sideband.Size, MaxSidebandOutputBytes));
    }

    private static void ExecuteJob(CpuContext ctx, AjmContextState contextState, AjmJob job)
    {
        if (job.Kind == AjmJobKind.GetStatistics)
        {
            ExecuteStatisticsJob(ctx, job);
            return;
        }

        var instance = FindInstance(contextState, job.InstanceId);
        if (instance is null)
        {
            Trace($"job_unknown_instance kind={job.Kind} instance=0x{job.InstanceId:X8}");
            WriteSidebandResult(ctx, job.SidebandOutput, AjmJobResult.Fatal | AjmJobResult.InvalidParameter, 0);
            return;
        }

        switch (job.Kind)
        {
            case AjmJobKind.Initialize:
                ExecuteInitializeJob(ctx, instance, job);
                break;
            case AjmJobKind.ClearContext:
                instance.Reset();
                Trace($"job_clear_context instance=0x{instance.InstanceId:X8}");
                WriteSidebandResult(ctx, job.SidebandOutput, 0, 0);
                break;
            case AjmJobKind.SetGaplessDecode:
                ExecuteSetGaplessJob(ctx, instance, job);
                break;
            case AjmJobKind.SetResampleParameters:
                ExecuteSetResampleJob(ctx, instance, job);
                break;
            case AjmJobKind.GetResampleInfo:
                ExecuteGetResampleInfoJob(ctx, instance, job);
                break;
            case AjmJobKind.Decode:
            case AjmJobKind.DecodeSplit:
                ExecuteDecodeJob(ctx, instance, job);
                break;
            default:
                WriteSidebandResult(ctx, job.SidebandOutput, AjmJobResult.UnsupportedFlag, 0);
                break;
        }
    }

    private static void ExecuteInitializeJob(CpuContext ctx, AjmInstanceState instance, AjmJob job)
    {
        var size = (int)Math.Min(job.SidebandInput.Size, MaxSidebandInputBytes);
        var parameters = new byte[Math.Max(size, (int)InitializeParametersBytes)];
        if (job.SidebandInput.Address == 0 ||
            !ctx.Memory.TryRead(job.SidebandInput.Address, parameters.AsSpan(0, size)))
        {
            Trace($"job_initialize unreadable instance=0x{instance.InstanceId:X8}");
            WriteSidebandResult(ctx, job.SidebandOutput, AjmJobResult.InvalidParameter, 0);
            return;
        }

        instance.ConfigData = parameters;
        instance.Decoder?.Dispose();
        instance.Decoder = null;
        instance.TotalDecodedSamples = 0;

        Trace(
            $"job_initialize instance=0x{instance.InstanceId:X8} codec={AjmCodecType.Name(instance.CodecType)} " +
            $"config={Convert.ToHexString(parameters.AsSpan(0, Math.Min(parameters.Length, 8)))}");
        WriteSidebandResult(ctx, job.SidebandOutput, 0, 0);
    }

    private static void ExecuteSetGaplessJob(CpuContext ctx, AjmInstanceState instance, AjmJob job)
    {
        Span<byte> parameters = stackalloc byte[(int)GaplessDecodeBytes];
        if (job.SidebandInput.Address == 0 ||
            !ctx.Memory.TryRead(job.SidebandInput.Address, parameters))
        {
            // The block is only eight bytes; a title that passes it by value leaves it in the
            // same register SharpEmu recorded as the pointer.
            BinaryPrimitives.WriteUInt64LittleEndian(parameters, job.Flags);
        }

        instance.GaplessTotalSamples = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        instance.GaplessSkipSamples = BinaryPrimitives.ReadUInt16LittleEndian(parameters[4..]);
        Trace(
            $"job_set_gapless instance=0x{instance.InstanceId:X8} " +
            $"total_samples={instance.GaplessTotalSamples} skip_samples={instance.GaplessSkipSamples}");
        WriteSidebandResult(ctx, job.SidebandOutput, 0, 0);
    }

    private static void ExecuteSetResampleJob(CpuContext ctx, AjmInstanceState instance, AjmJob job)
    {
        Span<byte> parameters = stackalloc byte[(int)ResampleParametersBytes];
        if (job.SidebandInput.Address == 0 ||
            !ctx.Memory.TryRead(job.SidebandInput.Address, parameters))
        {
            WriteSidebandResult(ctx, job.SidebandOutput, AjmJobResult.InvalidParameter, 0);
            return;
        }

        var ratio = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(parameters));
        instance.ResampleRatio = ratio;
        instance.ResampleFlags = BinaryPrimitives.ReadUInt32LittleEndian(parameters[4..]);

        // SharpEmu does not resample AJM output. Say so rather than reporting success and
        // then playing the stream back at the wrong rate.
        var unity = float.IsFinite(ratio) && Math.Abs(ratio - 1.0f) < 0.0001f;
        if (!unity)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ajm.resample_unsupported instance=0x{instance.InstanceId:X8} ratio={ratio}");
        }

        Trace($"job_set_resample instance=0x{instance.InstanceId:X8} ratio={ratio} flags=0x{instance.ResampleFlags:X}");
        WriteSidebandResult(ctx, job.SidebandOutput, unity ? 0 : AjmJobResult.UnsupportedFlag, 0);
    }

    private static void ExecuteGetResampleInfoJob(CpuContext ctx, AjmInstanceState instance, AjmJob job)
    {
        Span<byte> sideband = stackalloc byte[16];
        sideband.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(
            sideband[8..],
            BitConverter.SingleToInt32Bits(instance.ResampleRatio));
        BinaryPrimitives.WriteUInt32LittleEndian(sideband[12..], instance.ResampleFlags);
        WriteSideband(ctx, job.SidebandOutput, sideband);
        Trace($"job_get_resample_info instance=0x{instance.InstanceId:X8} ratio={instance.ResampleRatio}");
    }

    private static void ExecuteStatisticsJob(CpuContext ctx, AjmJob job)
    {
        // AjmSidebandResult(8) + AjmSidebandStatisticsEngine(16) +
        // AjmSidebandStatisticsEnginePerCodec(16) + AjmSidebandStatisticsMemory(24).
        Span<byte> sideband = stackalloc byte[(int)StatisticsSidebandBytes];
        sideband.Clear();

        // Engine usage: SharpEmu decodes on the host CPU, so report an idle co-processor
        // rather than fabricating load figures a title might throttle against.
        var memory = sideband[24..];
        BinaryPrimitives.WriteUInt32LittleEndian(memory, MaxInstanceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(memory[4..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(memory[8..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(memory[12..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(memory[16..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(memory[20..], 0x1000);

        WriteSideband(ctx, job.SidebandOutput, sideband);
        Trace($"job_get_statistics flags=0x{job.Flags:X16}");
    }

    private static void ExecuteDecodeJob(CpuContext ctx, AjmInstanceState instance, AjmJob job)
    {
        var inputSize = job.TotalInputSize;
        var outputSize = job.TotalOutputSize;
        if (inputSize == 0 && outputSize == 0)
        {
            WriteDecodeSideband(ctx, job.SidebandOutput, 0, 0, instance.TotalDecodedSamples, 0, 0);
            return;
        }

        var input = ReadGuestBuffers(ctx, job.InputBuffers, MaxSilentPcmBytes);
        var decoder = ResolveDecoder(instance);
        if (decoder is null)
        {
            // No decoder for this codec. Emit silence so the voice stays in sync and the
            // title keeps advancing its bitstream, but say clearly that it is silence.
            WriteSilence(ctx, job.OutputBuffers, outputSize);
            instance.TotalDecodedSamples += SamplesForBytes(instance, outputSize);
            WriteDecodeSideband(
                ctx,
                job.SidebandOutput,
                (int)Math.Min(inputSize, int.MaxValue),
                (int)Math.Min(outputSize, int.MaxValue),
                instance.TotalDecodedSamples,
                1,
                0);
            return;
        }

        var output = new byte[(int)Math.Min(outputSize, MaxSilentPcmBytes)];
        var decoded = decoder.Decode(input, output, out var inputConsumed, out var framesDecoded);
        if (decoded > 0)
        {
            WriteGuestBuffers(ctx, job.OutputBuffers, output.AsSpan(0, decoded));
        }

        if (decoded < output.Length)
        {
            ClearRemainingOutput(ctx, job.OutputBuffers, decoded, output.Length);
        }

        instance.TotalDecodedSamples += SamplesForBytes(instance, (ulong)decoded);

        var result = 0;
        if (decoded == 0)
        {
            // The decoder swallowed the input but has not produced PCM yet. PARTIAL_INPUT is
            // the codec's own "feed me more" status and is what titles retry against.
            result |= AjmJobResult.PartialInput;
        }

        WriteDecodeSideband(
            ctx,
            job.SidebandOutput,
            inputConsumed,
            decoded,
            instance.TotalDecodedSamples,
            (uint)framesDecoded,
            result);

        Trace(
            $"job_decode instance=0x{instance.InstanceId:X8} codec={AjmCodecType.Name(instance.CodecType)} " +
            $"in={inputSize} consumed={inputConsumed} out={outputSize} written={decoded} frames={framesDecoded}");
    }

    /// <summary>
    /// Gets (or lazily builds) the host decoder for an instance. Returns <see langword="null"/>
    /// when the codec has no SharpEmu implementation, which routes the job down the logged
    /// silence path.
    /// </summary>
    private static AjmDecoderSession? ResolveDecoder(AjmInstanceState instance)
    {
        if (instance.Decoder is { } existing)
        {
            return existing.IsUsable ? existing : null;
        }

        var session = AjmDecoderSession.TryCreate(
            instance.CodecType,
            instance.Channels,
            instance.Encoding,
            instance.ConfigData,
            out var reason);
        if (session is null)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ajm.decode_unsupported instance=0x{instance.InstanceId:X8} " +
                $"codec={AjmCodecType.Name(instance.CodecType)} reason={reason} " +
                "(this instance will output silence)");

            // Cache a poisoned session so the warning is emitted once per instance instead of
            // once per decode job on the audio hot path.
            instance.Decoder = AjmDecoderSession.Unsupported(reason);
            return null;
        }

        instance.Decoder = session;
        return session;
    }

    private static ulong SamplesForBytes(AjmInstanceState instance, ulong byteCount)
    {
        var channels = instance.Channels > 0 ? instance.Channels : 2;
        var bytesPerSample = instance.Encoding == AjmFormatEncoding.S16 ? 2 : 4;
        var frameBytes = (ulong)(channels * bytesPerSample);
        return frameBytes == 0 ? 0 : byteCount / frameBytes;
    }

    private static byte[] ReadGuestBuffers(
        CpuContext ctx,
        IReadOnlyList<AjmGuestBuffer> buffers,
        ulong limit)
    {
        ulong total = 0;
        foreach (var buffer in buffers)
        {
            total += buffer.Size;
        }

        if (total == 0)
        {
            return [];
        }

        var bytes = new byte[(int)Math.Min(total, limit)];
        var written = 0;
        foreach (var buffer in buffers)
        {
            var remaining = bytes.Length - written;
            if (remaining <= 0)
            {
                break;
            }

            var chunk = (int)Math.Min(buffer.Size, (ulong)remaining);
            if (!ctx.Memory.TryRead(buffer.Address, bytes.AsSpan(written, chunk)))
            {
                break;
            }

            written += chunk;
        }

        return written == bytes.Length ? bytes : bytes.AsSpan(0, written).ToArray();
    }

    private static void WriteGuestBuffers(
        CpuContext ctx,
        IReadOnlyList<AjmGuestBuffer> buffers,
        ReadOnlySpan<byte> source)
    {
        var offset = 0;
        foreach (var buffer in buffers)
        {
            if (offset >= source.Length)
            {
                break;
            }

            var chunk = (int)Math.Min(buffer.Size, (ulong)(source.Length - offset));
            if (!ctx.Memory.TryWrite(buffer.Address, source.Slice(offset, chunk)))
            {
                break;
            }

            offset += chunk;
        }
    }

    /// <summary>
    /// Zero-fills the tail of the PCM output buffers that the decoder did not fill, so a
    /// short decode leaves silence rather than the previous frame's samples.
    /// </summary>
    private static void ClearRemainingOutput(
        CpuContext ctx,
        IReadOnlyList<AjmGuestBuffer> buffers,
        int filled,
        int total)
    {
        var cursor = 0;
        foreach (var buffer in buffers)
        {
            var end = cursor + (int)Math.Min(buffer.Size, (ulong)int.MaxValue);
            if (end > filled)
            {
                var start = Math.Max(cursor, filled);
                var length = Math.Min(end, total) - start;
                if (length > 0)
                {
                    ClearGuestMemory(ctx, buffer.Address + (ulong)(start - cursor), (ulong)length);
                }
            }

            cursor = end;
            if (cursor >= total)
            {
                break;
            }
        }
    }

    private static void WriteSilence(CpuContext ctx, IReadOnlyList<AjmGuestBuffer> buffers, ulong total)
    {
        if (total > MaxSilentPcmBytes)
        {
            return;
        }

        foreach (var buffer in buffers)
        {
            ClearGuestMemory(ctx, buffer.Address, buffer.Size);
        }
    }

    private static void ClearAjmBatchError(CpuContext ctx, ulong errorAddress)
    {
        if (errorAddress == 0)
        {
            return;
        }

        Span<byte> error = stackalloc byte[AjmBatchErrorBytes];
        error.Clear();
        _ = ctx.Memory.TryWrite(errorAddress, error);
    }

    private static void WriteAjmBatchError(CpuContext ctx, ulong errorAddress, int errorCode, ulong jobAddress)
    {
        if (errorAddress == 0)
        {
            return;
        }

        Span<byte> error = stackalloc byte[AjmBatchErrorBytes];
        error.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(error, errorCode);
        BinaryPrimitives.WriteUInt64LittleEndian(error[8..], jobAddress);
        _ = ctx.Memory.TryWrite(errorAddress, error);
    }

    private static void WriteSidebandResult(CpuContext ctx, AjmGuestBuffer sideband, int result, int internalResult)
    {
        if (sideband.Address == 0 || sideband.Size < 8)
        {
            return;
        }

        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, result);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], internalResult);
        _ = ctx.Memory.TryWrite(sideband.Address, bytes);
    }

    private static void WriteSideband(CpuContext ctx, AjmGuestBuffer sideband, ReadOnlySpan<byte> bytes)
    {
        if (sideband.Address == 0 || sideband.Size == 0)
        {
            return;
        }

        var length = (int)Math.Min((ulong)bytes.Length, sideband.Size);
        _ = ctx.Memory.TryWrite(sideband.Address, bytes[..length]);
    }

    private static void WriteDecodeSideband(
        CpuContext ctx,
        AjmGuestBuffer sideband,
        int inputConsumed,
        int outputWritten,
        ulong totalDecodedSamples,
        uint frames,
        int result)
    {
        if (sideband.Address == 0)
        {
            return;
        }

        Span<byte> bytes = stackalloc byte[DecodeSidebandBytes];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, result);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], inputConsumed);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], outputWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], totalDecodedSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], frames);
        WriteSideband(ctx, sideband, bytes);
    }

    private static AjmInstanceState? FindInstance(AjmContextState contextState, uint instanceId)
    {
        var slot = instanceId & 0x3FFF;
        if (slot == 0)
        {
            return null;
        }

        lock (contextState.Gate)
        {
            return contextState.InstancesBySlot.TryGetValue(slot, out var instance) ? instance : null;
        }
    }

    /// <summary>
    /// Publishes the error-description table into guest memory once and returns the address of
    /// the requested entry. The strings have to live in guest-readable memory because the
    /// export hands back a <c>const char*</c>.
    /// </summary>
    private static ulong GetErrorStringAddress(CpuContext ctx, int errorCode)
    {
        var index = errorCode == 0 ? 0 : errorCode - OrbisAjmErrorUnknown + 1;
        if (index < 0 || index >= ErrorStringCount)
        {
            index = ErrorStringCount - 1;
        }

        var table = Volatile.Read(ref _errorStringTableAddress);
        if (table == 0)
        {
            if (ctx.Memory is not IGuestMemoryAllocator allocator ||
                !allocator.TryAllocateGuestMemory((ulong)(ErrorStringCount * ErrorStringStride), 0x10, out table))
            {
                return 0;
            }

            Span<byte> bytes = stackalloc byte[ErrorStringStride];
            for (var entry = 0; entry < ErrorStringCount; entry++)
            {
                var code = entry == 0 ? 0 : OrbisAjmErrorUnknown + entry - 1;
                var text = entry == ErrorStringCount - 1 ? "SCE_AJM_ERROR_UNKNOWN" : DescribeError(code);
                bytes.Clear();
                _ = Encoding.UTF8.GetBytes(text, bytes[..(ErrorStringStride - 1)]);
                if (!ctx.Memory.TryWrite(table + ((ulong)entry * ErrorStringStride), bytes))
                {
                    return 0;
                }
            }

            var published = Interlocked.CompareExchange(ref _errorStringTableAddress, table, 0);
            if (published != 0)
            {
                table = published;
            }
        }

        return table + ((ulong)index * ErrorStringStride);
    }

    // 0 (SCE_OK) + the 0x80930001..0x80930017 range + one trailing catch-all entry.
    private const int ErrorStringCount = 26;
    private const int ErrorStringStride = 48;

    private static void ClearGuestMemory(CpuContext ctx, ulong address, ulong byteCount)
    {
        if (address == 0 || byteCount == 0)
        {
            return;
        }

        var remaining = byteCount;
        var cursor = address;
        Span<byte> zero = stackalloc byte[256];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, (ulong)zero.Length);
            if (!ctx.Memory.TryWrite(cursor, zero[..chunk]))
            {
                return;
            }

            cursor += (ulong)chunk;
            remaining -= (ulong)chunk;
        }
    }

    private static ulong ReadStackArg64(CpuContext ctx, int index)
    {
        var address = ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong));
        return TryReadUInt64(ctx, address, out var value) ? value : 0;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool IsTraceEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (IsTraceEnabled)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }

    /// <summary>
    /// Dumps the raw integer argument registers for a batch-job entry point. The PS5 AJM
    /// prototypes are not public, so SharpEmu infers them; this trace is what a bring-up run
    /// uses to confirm or correct an ordering against a real title.
    /// </summary>
    private static void TraceArguments(CpuContext ctx, string name)
    {
        if (!IsTraceEnabled)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] ajm.{name} args rdi=0x{ctx[CpuRegister.Rdi]:X16} rsi=0x{ctx[CpuRegister.Rsi]:X16} " +
            $"rdx=0x{ctx[CpuRegister.Rdx]:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} " +
            $"r9=0x{ctx[CpuRegister.R9]:X16} stack0=0x{ReadStackArg64(ctx, 0):X16} stack1=0x{ReadStackArg64(ctx, 1):X16}");
    }
}
