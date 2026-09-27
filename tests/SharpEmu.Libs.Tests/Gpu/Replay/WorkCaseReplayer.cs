// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.Tests.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Tests.Gpu.Replay;

// Re-runs one captured draw or dispatch on a headless device through the same executor, pipeline
// cache and shader compiler the emulator uses, and writes what it produced next to the case. The
// point is that a wrong pass can be looked at, changed and looked at again without the game.
public static class WorkCaseReplayer
{
    private const ulong BackingHeadroom = 64UL * 1024 * 1024;

    // Replays every case directory under a root, newest first is irrelevant: all of them run.
    public static IReadOnlyList<string> FindCases(string root) =>
        !Directory.Exists(root)
            ? []
            : [.. Directory.EnumerateFiles(root, WorkCase.ManifestName, SearchOption.AllDirectories)
                .Select(static path => Path.GetDirectoryName(path)!)
                .OrderBy(static path => path, StringComparer.Ordinal)];

    // Creates the headless device itself; the tool calls this, while tests pass their fixture device.
    public static WorkCaseReplayResult Replay(string caseDirectory, WorkCaseReplayOptions options)
    {
        using var vulkan = HeadlessVulkan.TryCreate() ?? throw new InvalidOperationException(
            "No headless Vulkan device is available. On macOS point VK_ICD_FILENAMES at the MoltenVK ICD " +
            "and run the osx-x64 build, because the page watcher needs a 4 KiB host page.");
        return Replay(vulkan, caseDirectory, options);
    }

    internal static WorkCaseReplayResult Replay(HeadlessVulkan vulkan, string caseDirectory, WorkCaseReplayOptions options)
    {
        var manifest = WorkCase.Read(caseDirectory);
        // The emulator ends a fatal by killing the process; a replay turns it into an exception so
        // the tool can say which range a case is missing.
        using var fatal = new Scheduling.FatalScope();
        var output = options.OutputDirectory ?? Path.Combine(caseDirectory, "replay");
        Directory.CreateDirectory(output);
        var result = new WorkCaseReplayResult
        {
            CaseDirectory = caseDirectory,
            OutputDirectory = output,
            Kind = manifest.Kind,
        };
        var backing = BackingHeadroom + manifest.Memory.Aggregate(0ul, static (total, range) => total + range.Size);
        using var presenter = new PresenterUnderTest(vulkan, backingBytes: backing);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        RestoreMemory(harness, caseDirectory, manifest, result);

        var context = new CpuContext(harness.Memory, Generation.Gen5);
        var registry = BuildRegistry(context, manifest);
        var backend = new RecordingShaderBackend(output, result);
        var pipelines = new ShaderPipelineCache(context, (IShaderPipelineHost)presenter.Instance, backend, registry);
        var banks = RegisterBanks.Restore(
            static message => new InvalidOperationException(message),
            manifest.Banks.Context,
            manifest.Banks.Shader,
            manifest.Banks.UserConfig,
            manifest.Banks.IndexTypeAndSize,
            manifest.Banks.CompositeDepthSizeXy,
            manifest.Banks.UserDataMarker);
        var executor = new RenderExecutor(presenter.RenderHost, pipelines);
        var droppedBefore = DroppedWorkLog.DroppedCount;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            presenter.Run(() => Execute(executor, manifest, banks));
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        }
        catch (Exception exception)
        {
            // A case that is missing a range the resolvers read fails here. Report which one instead
            // of taking the process down: the whole point of a case is to be looked at. An exception
            // that is not a reported fatal carries its type and stack, because then it is a bug here.
            result.Notes.Add(exception is Scheduling.SchedulerFatalException
                ? $"the work did not run: {exception.Message}"
                : $"the work did not run: {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine($"[REPLAY][ERROR] {result.Notes[^1]}");
            if (exception is not Scheduling.SchedulerFatalException)
            {
                Console.Error.WriteLine(exception.StackTrace);
            }
        }
        finally
        {
            Attempt(harness.Finish, result, "finishing the submission");
            GuestGpuMemoryHook.Attach(null);
        }

        result.DroppedWork = DroppedWorkLog.DroppedCount - droppedBefore;
        result.Recorded = manifest.Kind == WorkCaseKind.Dispatch
            ? pipelines.ComputePipelineCount != 0
            : pipelines.GraphicsPipelineCount != 0;
        WriteShaderListings(context, manifest, output, result, options);
        if (result.Recorded)
        {
            ReadBackTargets(presenter, caseDirectory, manifest, output, result);
            DumpWrittenBuffers(harness, manifest, output, result);
        }
        else
        {
            result.Notes.Add("no target was read back: the case did not reach the device");
        }

        Attempt(harness.Shutdown, result, "shutting the stores down");
        // The stores write every GPU-modified image back on shutdown, so this is the memory the game
        // would read after the pass.
        DumpTargetMemory(harness, manifest, output, result);
        return result;
    }

    // Teardown after a failed replay must not hide the failure that caused it.
    private static void Attempt(Action work, WorkCaseReplayResult result, string what)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            result.Notes.Add($"{what} failed: {exception.Message}");
        }
    }

    // Drives the executor with the packet the case recorded, so the same path runs as in the game.
    private static void Execute(RenderExecutor executor, WorkCaseManifest manifest, RegisterBanks banks)
    {
        switch (manifest.Kind)
        {
            case WorkCaseKind.DrawIndexed when manifest.Draw is { } draw:
                executor.DrawIndexed(manifest.SubmitId, banks, new DrawIndexedArguments(
                    draw.PacketAddress,
                    draw.PacketOpcode,
                    draw.PacketCount,
                    draw.PacketIndexAddress,
                    draw.PacketIndexTypeAndSize,
                    draw.PacketInstanceCount,
                    draw.PacketBaseVertex,
                    draw.PacketFirstInstance,
                    Enum.TryParse<DrawOffsetSource>(draw.OffsetSource, out var indexedSource) ? indexedSource : DrawOffsetSource.Packet));
                break;
            case WorkCaseKind.DrawAuto when manifest.Draw is { } draw:
                executor.DrawAuto(manifest.SubmitId, banks, new DrawAutoArguments(
                    draw.PacketAddress,
                    draw.PacketOpcode,
                    draw.PacketCount,
                    draw.PacketInstanceCount,
                    draw.PacketFirstVertex,
                    draw.PacketFirstInstance,
                    Enum.TryParse<DrawOffsetSource>(draw.OffsetSource, out var autoSource) ? autoSource : DrawOffsetSource.Packet));
                break;
            case WorkCaseKind.Dispatch when manifest.Dispatch is { } dispatch:
                executor.Dispatch(manifest.SubmitId, banks, dispatch.GroupsX, dispatch.GroupsY, dispatch.GroupsZ,
                    dispatch.Initiator, dispatch.IndirectArgumentsAddress);
                break;
            default:
                throw new InvalidDataException($"The case has no arguments for its kind: kind={manifest.Kind}.");
        }
    }

    // Maps every captured range at its own address, one run of granules at a time, and writes its bytes.
    private static void RestoreMemory(Buffers.CacheHarness harness, string caseDirectory, WorkCaseManifest manifest, WorkCaseReplayResult result)
    {
        var granule = harness.MappingGranularity;
        var mapped = new List<(ulong Start, ulong End)>();
        foreach (var range in manifest.Memory.OrderBy(static entry => entry.Address))
        {
            var start = range.Address & ~(granule - 1);
            var end = (range.Address + range.Size + granule - 1) & ~(granule - 1);
            var cursor = start;
            while (cursor < end)
            {
                if (mapped.Exists(entry => cursor >= entry.Start && cursor < entry.End))
                {
                    cursor += granule;
                    continue;
                }

                var runEnd = cursor;
                while (runEnd < end && !mapped.Exists(entry => runEnd >= entry.Start && runEnd < entry.End))
                {
                    runEnd += granule;
                }

                if (!harness.TryMapBackedAt(cursor, runEnd - cursor, GuestPageProtection.Read | GuestPageProtection.Write, out var address, out var size, register: false))
                {
                    result.Notes.Add($"the range {range.Role} at 0x{cursor:X} could not be mapped");
                    Console.Error.WriteLine($"[REPLAY][WARN] {result.Notes[^1]}");
                    break;
                }

                mapped.Add((address, address + size));
                cursor = runEnd;
            }

            var bytes = File.ReadAllBytes(Path.Combine(caseDirectory, range.File));
            if (!harness.Memory.TryWriteBacking(range.Address, bytes))
            {
                result.Notes.Add($"the range {range.Role} at 0x{range.Address:X} could not be filled");
                Console.Error.WriteLine($"[REPLAY][WARN] {result.Notes[^1]}");
            }
        }

        // The watcher comes last: the restored bytes are the state the work started from, not writes
        // the caches have to chase back over what the replay renders.
        foreach (var (start, end) in mapped)
        {
            harness.RegisterGuestRange(start, end - start, GuestPageProtection.Read | GuestPageProtection.Write);
        }
    }

    // The headers the case recorded, so the pipeline cache resolves the same programs.
    private static ShaderHeaderRegistry BuildRegistry(CpuContext context, WorkCaseManifest manifest)
    {
        var headers = new Dictionary<ulong, ulong>();
        var fused = new Dictionary<ulong, FusedProgramParts>();
        foreach (var stage in manifest.Stages)
        {
            headers[stage.CodeAddress] = stage.HeaderAddress;
            if (stage.ContinuationAddress != 0)
            {
                fused[stage.CodeAddress] = new FusedProgramParts(stage.ContinuationAddress, stage.ContinuationHeaderAddress);
            }
        }

        return new ShaderHeaderRegistry(
            context,
            code => headers.TryGetValue(code, out var header) ? header : 0,
            code => fused.TryGetValue(code, out var parts) ? parts : null);
    }

    // The decoded program of every stage, from the bytes the case mapped back at their own address.
    private static void WriteShaderListings(
        CpuContext context, WorkCaseManifest manifest, string output, WorkCaseReplayResult result, WorkCaseReplayOptions options)
    {
        foreach (var stage in manifest.Stages)
        {
            var name = $"{stage.Stage.ToLowerInvariant()}-0x{stage.Hash:X16}";
            var text = new StringBuilder();
            text.AppendLine($"stage={stage.Stage} hash=0x{stage.Hash:X16} code=0x{stage.CodeAddress:X16} size=0x{stage.CodeSizeBytes:X}");
            if (Gen5ShaderTranslator.TryDecodeProgram(context, stage.CodeAddress, out var program, out var error) && program is not null)
            {
                foreach (var instruction in program.Instructions)
                {
                    text.AppendLine(
                        $"pc=0x{instruction.Pc:X} enc={instruction.Encoding} op={instruction.Opcode} " +
                        $"words={string.Join(',', instruction.Words.Select(static word => $"{word:X8}"))} " +
                        $"src={string.Join('/', instruction.Sources)} dst={string.Join('/', instruction.Destinations)} " +
                        $"control={instruction.Control?.ToString() ?? "-"}");
                }
            }
            else
            {
                text.AppendLine($"decode failed: {error}");
                result.Notes.Add($"the {stage.Stage} program did not decode: {error}");
            }

            if (options.StageDump)
            {
                text.AppendLine($"userDataBase={stage.UserDataBase} userData=[{string.Join(' ', stage.UserData.Select(static value => value.ToString("X8")))}]");
                for (var index = 0; index < stage.Buffers.Length; index++)
                {
                    text.AppendLine($"buffer{index}=[{string.Join(' ', stage.Buffers[index].Select(static word => word.ToString("X8")))}]");
                }

                for (var index = 0; index < stage.Images.Length; index++)
                {
                    text.AppendLine($"image{index}=[{string.Join(' ', stage.Images[index].Select(static word => word.ToString("X8")))}]");
                }
            }

            var path = Path.Combine(output, $"{name}.gcn.txt");
            File.WriteAllText(path, text.ToString());
            result.Files.Add(path);
        }
    }

    // Downloads every color target the replay rendered into and diffs it against the capture.
    private static void ReadBackTargets(
        PresenterUnderTest presenter, string caseDirectory, WorkCaseManifest manifest, string output, WorkCaseReplayResult result)
    {
        foreach (var target in manifest.Targets)
        {
            if (!target.Role.StartsWith("color", StringComparison.Ordinal))
            {
                result.Notes.Add($"{target.Role} was not read back: only color targets are downloaded");
                continue;
            }

            var words = manifest.Banks.Context.ColorTargets[(int)target.Slot];
            var resolution = ImageRequestBuilders.ColorTarget(in words, manifest.Banks.Context.RenderTargetMaskForSlot(target.Slot), 0, ignoreTargetMask: false);
            if (resolution is not { } resolved)
            {
                result.Notes.Add($"{target.Role} has no resolvable target registers");
                continue;
            }

            var image = presenter.Run(() =>
            {
                var request = resolved.Request;
                var identifier = presenter.Harness.Images.FindImage(ref request);
                var found = presenter.Harness.Images.GetImage(identifier);
                Console.Error.WriteLine(
                    $"[REPLAY][INFO] readback {target.Role} image={identifier.Index} " +
                    $"extent={found.Backing.Extent.Width}x{found.Backing.Extent.Height} format={found.Backing.Format} gpu={found.IsGpuModified}");
                return found;
            });
            var texels = presenter.Harness.ReadImageBytes(image);
            var format = image.Backing.Format.ToString();
            var binary = Path.Combine(output, $"{target.Role}-0x{target.Address:X}.bin");
            File.WriteAllBytes(binary, texels);
            result.Files.Add(binary);
            if (PngWriter.Supports(format))
            {
                var png = Path.Combine(output, $"{target.Role}-0x{target.Address:X}.png");
                PngWriter.Write(png, texels, image.Backing.Extent.Width, image.Backing.Extent.Height, format);
                result.Files.Add(png);
            }
            else
            {
                result.Notes.Add($"{target.Role} is {format}, which has no PNG layout here; the raw bytes are next to it");
            }

            result.Diffs.Add(Diff(target, texels, caseDirectory, manifest, image.Description.BytesPerBlock));
        }
    }

    private static WorkCaseTargetDiff Diff(
        WorkCaseTarget target, byte[] replayed, string caseDirectory, WorkCaseManifest manifest, uint bytesPerTexel)
    {
        var reference = manifest.Images.Find(image => image.Phase == "after" && image.Role == target.Role);
        var path = reference is null ? null : Path.Combine(caseDirectory, reference.File);
        if (path is null || !File.Exists(path))
        {
            return new WorkCaseTargetDiff { Role = target.Role, Bytes = replayed.Length };
        }

        var captured = File.ReadAllBytes(path);
        var length = Math.Min(captured.Length, replayed.Length);
        var stride = (int)Math.Max(bytesPerTexel, 1);
        var maximum = 0;
        var differing = 0;
        var texels = 0;
        for (var offset = 0; offset < length; offset += stride)
        {
            texels++;
            var different = false;
            for (var byteIndex = offset; byteIndex < Math.Min(offset + stride, length); byteIndex++)
            {
                var delta = Math.Abs(captured[byteIndex] - replayed[byteIndex]);
                maximum = Math.Max(maximum, delta);
                different |= delta != 0;
            }

            if (different)
            {
                differing++;
            }
        }

        return new WorkCaseTargetDiff
        {
            Role = target.Role,
            Reference = reference!.File,
            Bytes = length,
            MaxAbsoluteDifference = maximum,
            DifferingTexelPercent = texels == 0 ? 0 : 100.0 * differing / texels,
        };
    }

    // The guest bytes of every target after the stores flushed, for comparing against a capture.
    private static void DumpTargetMemory(Buffers.CacheHarness harness, WorkCaseManifest manifest, string output, WorkCaseReplayResult result)
    {
        foreach (var target in manifest.Targets)
        {
            var range = manifest.Memory.Find(entry => entry.Address == target.Address);
            if (range is null)
            {
                continue;
            }

            var bytes = new byte[range.Size];
            if (!harness.Memory.TryRead(target.Address, bytes))
            {
                result.Notes.Add($"the guest bytes of {target.Role} at 0x{target.Address:X} are not readable after the replay");
                continue;
            }

            var path = Path.Combine(output, $"guest-{target.Role}-0x{target.Address:X}.bin");
            File.WriteAllBytes(path, bytes);
            result.Files.Add(path);
        }
    }

    // The guest ranges the work writes, as they stand after the replay.
    private static void DumpWrittenBuffers(Buffers.CacheHarness harness, WorkCaseManifest manifest, string output, WorkCaseReplayResult result)
    {
        foreach (var buffer in manifest.WrittenBuffers)
        {
            if (buffer.Size == 0 || buffer.Size > int.MaxValue)
            {
                continue;
            }

            var bytes = new byte[buffer.Size];
            if (!harness.Memory.TryRead(buffer.Address, bytes))
            {
                result.Notes.Add($"the written buffer {buffer.Role} at 0x{buffer.Address:X} is not readable after the replay");
                continue;
            }

            var path = Path.Combine(output, $"written-{buffer.Role}-0x{buffer.Address:X}.bin");
            File.WriteAllBytes(path, bytes);
            result.Files.Add(path);
        }
    }

    // Saves every module the compiler produced, and its disassembly when spirv-dis is available.
    private sealed class RecordingShaderBackend(string output, WorkCaseReplayResult result) : IGuestGpuBackend
    {
        private readonly VulkanGuestGpuBackend _inner = new();
        private int _sequence;

        public string BackendName => _inner.BackendName;

        public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
        {
            if (!_inner.TryCompileProgram(request, out shader, out error))
            {
                result.Notes.Add($"the {request.Stage} program did not compile: {error}");
                return false;
            }

            var name = $"shader-{++_sequence:D2}-{request.Stage.ToString().ToLowerInvariant()}";
            var path = Path.Combine(output, $"{name}.{shader!.PayloadFileExtension}");
            File.WriteAllBytes(path, shader.Payload);
            result.Files.Add(path);
            if (SpirvDisassembler.TryDisassemble(path, Path.ChangeExtension(path, ".spvasm"), out var failure))
            {
                result.Files.Add(Path.ChangeExtension(path, ".spvasm"));
            }
            else if (failure is not null)
            {
                result.Notes.Add(failure);
            }

            return true;
        }

        public IGuestCompiledShader GetDepthOnlyFragmentShader() => _inner.GetDepthOnlyFragmentShader();

        public void CountShaderCompilation()
        {
        }

        public void EnsureStarted(uint width, uint height) => throw Unsupported();

        public void HideSplashScreen() => throw Unsupported();

        public void Submit(byte[] bgraFrame, uint width, uint height) => throw Unsupported();

        public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw Unsupported();

        public bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId) =>
            throw Unsupported();

        public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots) =>
            throw Unsupported();

        public IdleOutcome SubmitDone(ICpuMemory memory) => throw Unsupported();

        public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) => throw Unsupported();

        public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => false;

        public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() => throw Unsupported();

        public void RequestClose() => throw Unsupported();

        private static Exception Unsupported() => new NotSupportedException("The replay backend presents nothing.");
    }
}

// Finds spirv-dis on the path or in an installed Vulkan SDK; the replay works without it.
internal static class SpirvDisassembler
{
    private static readonly string? ToolPath = Locate();

    public static bool TryDisassemble(string spirv, string listing, out string? failure)
    {
        failure = null;
        if (ToolPath is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(ToolPath, ["--no-header", "-o", listing, spirv])
            {
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            failure = $"spirv-dis refused {Path.GetFileName(spirv)}: {errors.Trim()}";
            return false;
        }
        catch (Exception exception)
        {
            failure = $"spirv-dis could not run: {exception.Message}";
            return false;
        }
    }

    private static string? Locate()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, "spirv-dis");
            if (directory.Length != 0 && File.Exists(candidate))
            {
                return candidate;
            }
        }

        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VulkanSDK");
        if (!Directory.Exists(sdk))
        {
            return null;
        }

        return Directory.EnumerateDirectories(sdk)
            .Select(static version => Path.Combine(version, "macOS", "bin", "spirv-dis"))
            .Where(File.Exists)
            .OrderDescending(StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
