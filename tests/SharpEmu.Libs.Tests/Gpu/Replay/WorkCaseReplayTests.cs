// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Replay;

// Turns captured cases into regression gates: each one replays on the headless device and must reach
// it without the executor dropping the work. SHARPEMU_REPLAY_CASES names a directory of cases, so a
// case checked in next to the repository, or one just captured from a game, becomes a test.
[Collection(SchedulingStateCollection.Name)]
public sealed class WorkCaseReplayTests : IClassFixture<HeadlessVulkanFixture>
{
    public const string CasesVariable = "SHARPEMU_REPLAY_CASES";

    private readonly HeadlessVulkan? _vulkan;

    public WorkCaseReplayTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void SyntheticCase_ReplaysAndDiffsAgainstItsOwnResult()
    {
        if (!GatePrerequisites.Ready(_vulkan) || !_vulkan.SupportsDynamicRendering)
        {
            return;
        }

        // SHARPEMU_REPLAY_KEEP=1 leaves the generated case and its replay output on disk to look at.
        var keep = Environment.GetEnvironmentVariable("SHARPEMU_REPLAY_KEEP") == "1";
        var root = Path.Combine(keep ? Path.Combine(Path.GetTempPath(), "sharpemu-replay") : Path.GetTempPath(), $"synthetic-{Guid.NewGuid():N}");
        try
        {
            var caseDirectory = SyntheticWorkCase.Write(root);
            var first = WorkCaseReplayer.Replay(_vulkan, caseDirectory, new WorkCaseReplayOptions { StageDump = true });
            Assert.True(first.Recorded, first.Describe());
            Assert.Equal(0, first.DroppedWork);
            var target = Assert.Single(first.Files, static path => path.EndsWith(Path.DirectorySeparatorChar + "color0-0x210100000.bin", StringComparison.Ordinal));
            var texels = File.ReadAllBytes(target);

            // The triangle covers the target, so the replay rendered the colour the pixel program exports.
            var center = (int)((SyntheticWorkCase.CenterY * SyntheticWorkCase.Size) + SyntheticWorkCase.CenterX) * 4;
            Assert.Equal(0xFF0000FFu, BitConverter.ToUInt32(texels, center));

            // The same case replayed again must match the result it was given as a reference.
            SyntheticWorkCase.AdoptReference(caseDirectory, target);
            var second = WorkCaseReplayer.Replay(_vulkan, caseDirectory,
                new WorkCaseReplayOptions { OutputDirectory = Path.Combine(root, "replay-2") });
            var diff = Assert.Single(second.Diffs);
            Assert.Equal("after-color0.bin", diff.Reference);
            Assert.Equal(0, diff.MaxAbsoluteDifference);
            Assert.Equal(0, diff.DifferingTexelPercent);
        }
        finally
        {
            if (keep)
            {
                Console.Error.WriteLine($"[TEST][INFO] The synthetic case stayed at '{root}'.");
            }
            else
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // A real game's base pass fetches its vertices through pointer tables the user scalars name, and a
    // case that does not carry those tables cannot replay. This pins both halves: the tables travel
    // with a case, and a case missing one says which address it could not read instead of crashing.
    [Fact]
    public void FetchCase_CarriesTheTablesItsUserScalarsPointAt()
    {
        if (!GatePrerequisites.Ready(_vulkan) || !_vulkan.SupportsDynamicRendering)
        {
            return;
        }

        var keep = Environment.GetEnvironmentVariable("SHARPEMU_REPLAY_KEEP") == "1";
        var root = Path.Combine(keep ? Path.Combine(Path.GetTempPath(), "sharpemu-replay") : Path.GetTempPath(), $"fetch-{Guid.NewGuid():N}");
        try
        {
            var caseDirectory = SyntheticWorkCase.Write(root, vertexTables: true);
            var complete = WorkCaseReplayer.Replay(_vulkan, caseDirectory, new WorkCaseReplayOptions());
            Assert.True(complete.Recorded, complete.Describe());
            Assert.Equal(0, complete.DroppedWork);

            // A copy without the attribute table the vertex header reaches through a user scalar
            // pointer; the complete case stays on disk for the capture side to be checked against.
            var brokenDirectory = Path.Combine(root, "without-attribute-table");
            Directory.CreateDirectory(brokenDirectory);
            foreach (var file in Directory.EnumerateFiles(caseDirectory))
            {
                File.Copy(file, Path.Combine(brokenDirectory, Path.GetFileName(file)));
            }

            var manifest = WorkCase.Read(brokenDirectory);
            Assert.Equal(1, manifest.Memory.RemoveAll(range => range.Role == SyntheticWorkCase.AttributeTableRole));
            File.WriteAllText(Path.Combine(brokenDirectory, WorkCase.ManifestName),
                System.Text.Json.JsonSerializer.Serialize(manifest, WorkCase.Json));
            var broken = WorkCaseReplayer.Replay(_vulkan, brokenDirectory, new WorkCaseReplayOptions());
            Assert.False(broken.Recorded, broken.Describe());
            Assert.Contains(broken.Notes, note => note.Contains("vertex attribute table is unreadable", StringComparison.Ordinal));
        }
        finally
        {
            if (keep)
            {
                Console.Error.WriteLine($"[TEST][INFO] The fetch case stayed at '{root}'.");
            }
            else
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Every case under SHARPEMU_REPLAY_CASES must still replay; the gate passes when none are configured.
    [Fact]
    public void ConfiguredCases_ReplayWithoutDroppedWork()
    {
        var root = Environment.GetEnvironmentVariable(CasesVariable);
        if (root is null)
        {
            return;
        }

        var cases = WorkCaseReplayer.FindCases(root);
        Assert.False(cases.Count == 0, $"{CasesVariable} names '{root}', which holds no case directories.");
        if (!GatePrerequisites.Ready(_vulkan) || !_vulkan.SupportsDynamicRendering)
        {
            return;
        }

        var failures = new List<string>();
        foreach (var directory in cases)
        {
            var result = WorkCaseReplayer.Replay(_vulkan, directory, new WorkCaseReplayOptions());
            Console.Error.WriteLine($"[TEST][INFO] {result.Describe()}");
            if (!result.Recorded || result.DroppedWork != 0)
            {
                failures.Add(result.Describe());
            }
        }

        Assert.Empty(failures);
    }
}
