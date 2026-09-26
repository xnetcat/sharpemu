// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Rendering;

// The on-disk description of one captured draw or dispatch. The capture in the emulator writes it
// and the replay tool reads it back, so both sides share these types instead of a parser each.
public static class WorkCase
{
    public const int FormatVersion = 1;

    public const string ManifestName = "manifest.json";

    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static WorkCaseManifest Read(string caseDirectory)
    {
        var path = Path.Combine(caseDirectory, ManifestName);
        var manifest = JsonSerializer.Deserialize<WorkCaseManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"The case manifest is empty: path={path}.");
        if (manifest.Version != FormatVersion)
        {
            throw new InvalidDataException($"The case manifest version is unknown: path={path} version={manifest.Version}.");
        }

        return manifest;
    }
}

public enum WorkCaseKind
{
    DrawAuto,
    DrawIndexed,
    Dispatch,
}

// The three register banks as the draw saw them; the replay rebuilds the banks from these.
public sealed class WorkCaseBanks
{
    public ContextRegisters Context { get; set; } = new();
    public ShaderProgramRegisters Shader { get; set; } = new();
    public UserConfigRegisters UserConfig { get; set; } = new();
    public uint IndexTypeAndSize { get; set; }
    public uint? CompositeDepthSizeXy { get; set; }
    public UserScalarKind UserDataMarker { get; set; }
}

// One guest range the case carries, as a file of raw bytes next to the manifest.
public sealed class WorkCaseRange
{
    public string Role { get; set; } = string.Empty;
    public ulong Address { get; set; }
    public ulong Size { get; set; }
    public string File { get; set; } = string.Empty;

    // Set when the range was cut short by the mapped extent or by the byte budget.
    public ulong RequestedSize { get; set; }
}

// One shader stage of the case: what the header said, and the raw program bytes.
public sealed class WorkCaseStage
{
    public string Stage { get; set; } = string.Empty;
    public ulong Hash { get; set; }
    public ulong CodeAddress { get; set; }
    public ulong HeaderAddress { get; set; }
    public uint CodeSizeBytes { get; set; }
    public ulong ContinuationAddress { get; set; }
    public uint ContinuationSizeBytes { get; set; }
    public ulong UserDataAddress { get; set; }
    public ulong InputSemanticsAddress { get; set; }
    public uint InputSemanticsCount { get; set; }
    public uint ScratchDwords { get; set; }
    public string File { get; set; } = string.Empty;
    public string? ContinuationFile { get; set; }

    // The descriptor words and user scalars the stage was bound with.
    public uint[] UserData { get; set; } = [];
    public uint UserDataBase { get; set; }
    public uint[][] Buffers { get; set; } = [];
    public uint[][] Images { get; set; } = [];
    public uint[][] Samplers { get; set; } = [];
    public uint[] FlattenedResourceTable { get; set; } = [];
}

// A host image the capture downloaded, before or after the work ran.
public sealed class WorkCaseImage
{
    public string Phase { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public ulong Address { get; set; }
    public string File { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Depth { get; set; }
    public uint Layers { get; set; }
    public uint TexelBytes { get; set; }
}

public sealed class WorkCaseDraw
{
    public bool Indexed { get; set; }
    public uint Count { get; set; }
    public uint InstanceCount { get; set; }
    public uint FirstVertex { get; set; }
    public uint FirstInstance { get; set; }
    public int BaseVertex { get; set; }
    public ulong IndexAddress { get; set; }
    public uint IndexTypeAndSize { get; set; }
    public uint PrimitiveType { get; set; }
    public string Topology { get; set; } = string.Empty;
    public bool PrimitiveRestart { get; set; }
    public ulong PacketAddress { get; set; }
    public string OffsetSource { get; set; } = string.Empty;
}

public sealed class WorkCaseDispatch
{
    public uint GroupsX { get; set; }
    public uint GroupsY { get; set; }
    public uint GroupsZ { get; set; }
    public uint Initiator { get; set; }
    public ulong IndirectArgumentsAddress { get; set; }
    public uint ThreadsX { get; set; }
    public uint ThreadsY { get; set; }
    public uint ThreadsZ { get; set; }
}

// One color or depth target the draw rendered into, for reading the case without a replay.
public sealed class WorkCaseTarget
{
    public string Role { get; set; } = string.Empty;
    public uint Slot { get; set; }
    public ulong Address { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Depth { get; set; }
    public uint Samples { get; set; }
    public string Format { get; set; } = string.Empty;
}

public sealed class WorkCaseManifest
{
    public int Version { get; set; } = WorkCase.FormatVersion;
    public WorkCaseKind Kind { get; set; }
    public string Selector { get; set; } = string.Empty;
    public uint Occurrence { get; set; }
    public ulong SubmitId { get; set; }
    public string CapturedUtc { get; set; } = string.Empty;
    public WorkCaseDraw? Draw { get; set; }
    public WorkCaseDispatch? Dispatch { get; set; }
    public List<WorkCaseTarget> Targets { get; set; } = [];
    public WorkCaseBanks Banks { get; set; } = new();
    public List<WorkCaseStage> Stages { get; set; } = [];
    public List<WorkCaseRange> Memory { get; set; } = [];
    public List<WorkCaseImage> Images { get; set; } = [];

    // The guest ranges the work writes; a replay dumps them so they can be diffed by hand.
    public List<WorkCaseRange> WrittenBuffers { get; set; } = [];

    // Everything the capture could not take, in plain words, so a replay mismatch has an explanation.
    public List<string> Truncated { get; set; } = [];

    public ulong CapturedBytes { get; set; }

    public WorkCaseStage? StageOf(ulong codeAddress) => Stages.Find(stage => stage.CodeAddress == codeAddress);

    // The stage resources as the shader compiler consumes them.
    public static ResourceSnapshot SnapshotOf(WorkCaseStage stage) => new()
    {
        Buffers = stage.Buffers,
        Images = stage.Images,
        Samplers = stage.Samplers,
        FlattenedResourceTable = stage.FlattenedResourceTable,
        UserData = stage.UserData,
    };
}
