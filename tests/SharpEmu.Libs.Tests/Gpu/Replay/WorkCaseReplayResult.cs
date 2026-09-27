// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Tests.Gpu.Replay;

public sealed class WorkCaseReplayOptions
{
    // Where the replay writes its shaders, listings and images; the case's own replay/ by default.
    public string? OutputDirectory { get; init; }

    // Also write each stage's resource plan and binding layout.
    public bool StageDump { get; init; }

    // Run the work this many more times after the recorded replay, each drained on its own, and
    // report the host-clock time per run: GPU timestamps are not trustworthy on MoltenVK.
    public int TimeRepeats { get; init; }
}

// How one replayed target compares with what the capture recorded after the work ran.
public sealed class WorkCaseTargetDiff
{
    public string Role { get; init; } = string.Empty;
    public string? Reference { get; init; }
    public int Bytes { get; init; }
    public int MaxAbsoluteDifference { get; init; }
    public double DifferingTexelPercent { get; init; }
    public string Summary =>
        Reference is null
            ? $"{Role}: no captured reference"
            : $"{Role}: maxAbsDiff={MaxAbsoluteDifference} differingTexels={DifferingTexelPercent:F3}% bytes={Bytes}";
}

public sealed class WorkCaseReplayResult
{
    public string CaseDirectory { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public WorkCaseKind Kind { get; init; }

    // True when the host built a pipeline for the work, so the case reached the device.
    public bool Recorded { get; set; }

    // Draws and dispatches the executor refused during the replay; a case must drop none.
    public long DroppedWork { get; set; }

    public List<string> Notes { get; } = [];

    public List<string> Files { get; } = [];

    public List<WorkCaseTargetDiff> Diffs { get; } = [];

    public string Describe() =>
        $"{Path.GetFileName(CaseDirectory)}: kind={Kind} recorded={Recorded} dropped={DroppedWork} " +
        $"files={Files.Count} diffs=[{string.Join("; ", Diffs.Select(static diff => diff.Summary))}] " +
        $"notes=[{string.Join("; ", Notes)}]";
}
