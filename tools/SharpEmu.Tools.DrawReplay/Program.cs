// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Replays draw and dispatch cases captured with SHARPEMU_CAPTURE_WORK on a headless Vulkan device,
// so a wrong pass is debugged offline instead of by launching the game again.

using SharpEmu.Libs.Tests.Gpu.Replay;

var cases = new List<string>();
string? output = null;
var stageDump = false;
var timeRepeats = 0;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--stage-dump":
            stageDump = true;
            break;
        case "--time" when index + 1 < args.Length:
            timeRepeats = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--out" when index + 1 < args.Length:
            output = args[++index];
            break;
        case "--help" or "-h":
            Usage();
            return 0;
        default:
            if (args[index].StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {args[index]}");
                Usage();
                return 2;
            }

            cases.Add(args[index]);
            break;
    }
}

if (cases.Count == 0)
{
    Usage();
    return 2;
}

// A directory of cases replays as a set; one case directory replays on its own.
var directories = new List<string>();
foreach (var entry in cases)
{
    if (File.Exists(Path.Combine(entry, "manifest.json")))
    {
        directories.Add(entry);
        continue;
    }

    var found = WorkCaseReplayer.FindCases(entry);
    if (found.Count == 0)
    {
        Console.Error.WriteLine($"No case directory under '{entry}': a case holds a manifest.json.");
        return 2;
    }

    directories.AddRange(found);
}

if (output is not null && directories.Count > 1)
{
    Console.Error.WriteLine("--out takes one case; several cases write into their own replay directories.");
    return 2;
}

var failures = 0;
foreach (var directory in directories)
{
    try
    {
        var result = WorkCaseReplayer.Replay(directory, new WorkCaseReplayOptions
        {
            OutputDirectory = output,
            StageDump = stageDump,
            TimeRepeats = timeRepeats,
        });
        Console.WriteLine(result.Describe());
        Console.WriteLine($"  output: {result.OutputDirectory}");
        foreach (var file in result.Files)
        {
            Console.WriteLine($"    {Path.GetFileName(file)}");
        }

        if (!result.Recorded || result.DroppedWork != 0)
        {
            failures++;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"{Path.GetFileName(directory)}: {exception.Message}");
        failures++;
    }
}

return failures == 0 ? 0 : 1;

static void Usage()
{
    Console.Error.WriteLine(
        """
        Usage: SharpEmu.Tools.DrawReplay [--stage-dump] [--out <dir>] <case-dir-or-root>...

          --stage-dump  also write each stage's descriptor words and user scalars
          --out <dir>   write one case's output here instead of <case>/replay
          --time <n>    run the work n more times, drained one by one, and print host-clock times

        Capture cases from a running game with:
          SHARPEMU_CAPTURE_WORK=0x<program-hash>[@N] SHARPEMU_CAPTURE_DIR=<dir> ...

        On macOS the device needs the MoltenVK ICD and a 4 KiB host page:
          VK_ICD_FILENAMES=~/VulkanSDK/<version>/macOS/share/vulkan/icd.d/MoltenVK_icd.json
          and an osx-x64 build, as the emulator itself runs.
        """);
}
