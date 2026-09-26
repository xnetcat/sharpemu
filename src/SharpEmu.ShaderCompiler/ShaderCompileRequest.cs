// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler;

// One fixed-function vertex attribute a fetch instruction reads instead of its buffer.
public sealed record ShaderVertexInput(
    uint Pc,
    uint Location,
    uint FetchComponentCount,
    uint ComponentCount,
    uint NumberFormat,
    uint DestinationSelect,
    bool PerInstance,
    IReadOnlyList<uint> AliasPcs)
{
    public ShaderVertexInput(
        uint pc,
        uint location,
        uint componentCount,
        uint numberFormat,
        bool perInstance,
        IReadOnlyList<uint> aliasPcs)
        : this(
            pc,
            location,
            componentCount,
            componentCount,
            numberFormat,
            componentCount switch
            {
                1u => 4u,
                2u => 4u | (5u << 3),
                3u => 4u | (5u << 3) | (6u << 6),
                4u => 4u | (5u << 3) | (6u << 6) | (7u << 9),
                _ => 0u,
            },
            perInstance,
            aliasPcs)
    {
    }
}

public readonly record struct ShaderClipSpaceTransform(
    bool Enabled,
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY,
    float HalfExtentX,
    float HalfExtentY);

// Everything an emitter needs to compile one permutation of a program: the decoded
// program, its resource plan applied to one specialization, and the binding layout.
public sealed class ShaderCompileRequest
{
    public const uint UnboundedThreadCount = uint.MaxValue;
    public const int WrittenRangeDwordCount = 3;
    public bool TraceDeviceAddressFaults { get; init; }

    public ShaderCompileRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout bindings)
    {
        Program = plan.Graph.Program;
        Stage = plan.Stage;
        Hash = plan.Hash;
        Memory = plan.Memory;
        Resources = resources;
        Bindings = bindings;
        UserDataBase = plan.UserDataBase;
        UserDataCount = plan.UserDataCount;
        UsesFlattenedTable = RequiresFlattenedTable(plan, resources);
        UsesGlobalDataShare = BindingLayout.UsesGlobalDataShare(Program);
        ReadsShaderBase = BindingLayout.ReadsShaderBase(Program);

        FlattenedSlotByMemoryIndex = new Dictionary<int, uint>(plan.FlattenedSlotByMemoryIndex);
        IndirectKeyMemoryIndices = plan.IndirectImages.Select(access => access.Key.MemoryIndex).ToHashSet();
        IndirectOffsetKeyMemoryIndices = plan.IndirectImages.Where(access => access.KeyIsAddressOffset)
            .Select(access => access.Key.MemoryIndex).ToHashSet();
        IndirectRootByMemoryIndex = plan.IndirectImages.ToDictionary(access => access.MemoryIndex, access => access.Key.MemoryIndex);

        var writtenSlots = new Dictionary<int, uint>();
        foreach (var range in plan.DeviceAddressRanges)
        {
            if (!range.Written || !plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            foreach (var memoryIndex in range.MemoryIndices)
            {
                writtenSlots[memoryIndex] = slot;
            }
        }

        WrittenRangeSlotByMemoryIndex = writtenSlots;
    }

    // The flattened table is bound when host reads, written ranges or indirect mappings fill it.
    public static bool RequiresFlattenedTable(ShaderResourcePlan plan, SpecializedResourceInfo resources) =>
        plan.TableReads.Count != 0 || plan.WrittenRangeCount != 0 ||
        resources.Info.Images.Any(image => image.IndirectSearchIterations != 0);

    public Gen5ShaderProgram Program { get; }
    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public MemoryAccessTable Memory { get; }
    public SpecializedResourceInfo Resources { get; }
    public BindingLayout Bindings { get; }
    public uint UserDataBase { get; }
    public uint UserDataCount { get; }
    public bool UsesFlattenedTable { get; }
    public bool UsesGlobalDataShare { get; }
    public bool ReadsShaderBase { get; }

    // Host-flattened scalar reads: memory index → flattened table slot.
    public IReadOnlyDictionary<int, uint> FlattenedSlotByMemoryIndex { get; }

    // Scalar reads whose loaded dword selects an indirect image at a later instruction.
    public IReadOnlySet<int> IndirectKeyMemoryIndices { get; }

    public IReadOnlySet<int> IndirectOffsetKeyMemoryIndices { get; }

    // Indirect image accesses: memory index → the memory index of the key read.
    public IReadOnlyDictionary<int, int> IndirectRootByMemoryIndex { get; }

    // Written device-address accesses: memory index → the flattened slot of their range.
    public IReadOnlyDictionary<int, uint> WrittenRangeSlotByMemoryIndex { get; }

    public uint WaveSize { get; init; } = 32;

    // How this program takes part in an emulated NGG geometry stage, and the parameter
    // exports each vertex record of that stage carries.
    public NggEmulationMode NggMode { get; init; }
    public uint NggParamCount { get; init; }
    public uint ScratchDwords { get; init; }
    public bool EnableGraphicsSubgroupOperations { get; init; } = true;
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; init; }

    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; init; } = [];
    public uint PixelInputEnable { get; init; }
    public uint PixelCustomInterpolationMask { get; init; }

    // False when the device cannot read one vertex's value of a pixel input (PerVertexKHR).
    // MoltenVK advertises fragment shader barycentrics but cannot translate PerVertexKHR to MSL.
    public bool SupportsPerVertexPixelInputs { get; init; } = true;
    public uint PixelInputAddress { get; init; }
    public IReadOnlyList<uint>? PixelInputCntl { get; init; }

    public int RequiredVertexOutputCount { get; init; }
    public IReadOnlyList<ShaderVertexInput> VertexInputs { get; init; } = [];
    public uint PositionExportControl { get; init; }
    public ShaderClipSpaceTransform ClipSpace { get; init; }

    public uint LocalSizeX { get; init; } = 1;
    public uint LocalSizeY { get; init; } = 1;
    public uint LocalSizeZ { get; init; } = 1;

    // Fixed bounds for standalone modules; runtime-limit layouts read the dispatch data.
    public uint ThreadCountX { get; init; } = UnboundedThreadCount;
    public uint ThreadCountY { get; init; } = UnboundedThreadCount;
    public uint ThreadCountZ { get; init; } = UnboundedThreadCount;
}

// A merged export and geometry stage runs as a compute workgroup that stores its exports to a
// record buffer (Compute); a vertex program then reads those records back for rasterization (Replay).
public enum NggEmulationMode
{
    None,
    Compute,
    Replay,
    // Diagnostic: an ordinary compute program that also stores the NGG debug slots of every
    // thread to the output buffer (SHARPEMU_DEBUG_COMPUTE_HASH).
    Debug,
}

// The record layout both emulation programs share, in dwords per exported lane.
public static class NggRecordLayout
{
    public const uint WaveLanes = 64;
    public const uint PrimitiveDword = 0;
    public const uint Position0Dword = 4;
    public const uint Position1Dword = 8;
    public const uint FirstParamDword = 12;
    public const uint InputHeaderDwords = 4;
    public const uint InputLaneDwords = 9;
    public const uint InputGroupDwords = InputHeaderDwords + WaveLanes * InputLaneDwords;

    // Diagnostic: SHARPEMU_NGG_DEBUG_PCS lists program counters (hex) after which every lane
    // stores registers into DebugSlotDwords-dword slots appended to its record.
    public const uint DebugSlotDwords = 16;
    public static readonly uint[] DebugPcs = (Environment.GetEnvironmentVariable("SHARPEMU_NGG_DEBUG_PCS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static value => Convert.ToUInt32(value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), 16))
        .ToArray();

    public static uint RecordDwords(uint paramCount) =>
        FirstParamDword + 4 * paramCount + (uint)DebugPcs.Length * DebugSlotDwords;
}
