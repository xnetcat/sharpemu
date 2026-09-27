// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.Rendering;

// Collects one case in memory and writes the directory when the work is recorded. Ranges are merged
// and clamped page by page: a case that cannot hold everything says what it dropped instead of
// looking complete.
internal sealed class WorkCaptureBuilder
{
    private const ulong PageBytes = 0x1000;

    private readonly WorkCaptureGuestReader _readGuest;
    private readonly ulong _budget;
    private readonly List<(string Role, ulong Address, ulong Size)> _requested = [];

    internal WorkCaptureBuilder(
        WorkCaseKind kind,
        ulong submitId,
        WorkCaptureSelector selector,
        WorkCaptureGuestReader readGuest,
        string root,
        ulong budget)
    {
        _readGuest = readGuest;
        _budget = budget;
        var name = $"{kind.ToString().ToLowerInvariant()}-{selector.Text}"
            .Replace('@', '-').Replace("0x", string.Empty, StringComparison.Ordinal);
        Directory = Path.Combine(root, $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        Manifest = new WorkCaseManifest
        {
            Kind = kind,
            SubmitId = submitId,
            Selector = selector.Text,
            Occurrence = selector.Occurrence,
            CapturedUtc = DateTime.UtcNow.ToString("O"),
        };
    }

    public WorkCaseManifest Manifest { get; }

    public string Directory { get; }

    public void Note(string message) => Manifest.Truncated.Add(message);

    // Records a guest range the case needs; overlapping requests are merged when the case is written.
    public void AddRange(string role, ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return;
        }

        _requested.Add((role, address, size));
    }

    // The packet the executor was called with, recorded by the caller once the draw is emitted.
    public void SetPacket(in GpuCommands.DrawIndexedArguments arguments)
    {
        if (Manifest.Draw is not { } draw)
        {
            return;
        }

        draw.PacketAddress = arguments.PacketAddress;
        draw.PacketOpcode = arguments.Opcode;
        draw.PacketCount = arguments.IndexCount;
        draw.PacketInstanceCount = arguments.InstanceCount;
        draw.PacketBaseVertex = arguments.BaseVertex;
        draw.PacketFirstInstance = arguments.FirstInstance;
        draw.PacketIndexAddress = arguments.IndexAddress;
        draw.PacketIndexTypeAndSize = arguments.IndexTypeAndSize;
        draw.OffsetSource = arguments.OffsetSource.ToString();
    }

    public void SetPacket(in GpuCommands.DrawAutoArguments arguments)
    {
        if (Manifest.Draw is not { } draw)
        {
            return;
        }

        draw.PacketAddress = arguments.PacketAddress;
        draw.PacketOpcode = arguments.Opcode;
        draw.PacketCount = arguments.VertexCount;
        draw.PacketInstanceCount = arguments.InstanceCount;
        draw.PacketFirstVertex = arguments.FirstVertex;
        draw.PacketFirstInstance = arguments.FirstInstance;
        draw.OffsetSource = arguments.OffsetSource.ToString();
    }

    public bool TryReadUInt64(ulong address, out ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!_readGuest(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    // The register banks, copied so later packets in the same frame cannot change what the case says.
    public void SetBanks(RegisterBanks banks) => Manifest.Banks = new WorkCaseBanks
    {
        Context = banks.Context.Copy(),
        Shader = banks.Shader.Copy(),
        UserConfig = banks.UserConfig.Copy(),
        IndexTypeAndSize = banks.IndexTypeAndSize,
        CompositeDepthSizeXy = banks.CompositeDepthSizeXy,
        UserDataMarker = banks.UserDataMarker,
    };

    // One bound stage: its header, its program bytes and the descriptor words it was bound with.
    public void AddStage(ShaderStageResources resources, ulong codeAddress)
    {
        if (resources.Program is not { } program)
        {
            return;
        }

        var source = WorkCapture.NotedShader(codeAddress);
        var label = program.Stage.ToString().ToLowerInvariant();
        var stage = new WorkCaseStage
        {
            Stage = program.Stage.ToString(),
            Hash = program.Hash,
            CodeAddress = codeAddress,
            UserDataBase = program.UserDataBase,
            UserData = resources.Resources.UserData,
            Buffers = resources.Resources.Buffers,
            Images = resources.Resources.Images,
            Samplers = resources.Resources.Samplers,
            FlattenedResourceTable = resources.Resources.FlattenedResourceTable,
        };
        if (source is null)
        {
            Note($"the {label} stage has no registered header; its program bytes are missing");
        }
        else
        {
            var registered = source.Registered;
            stage.HeaderAddress = registered.HeaderAddress;
            stage.CodeSizeBytes = registered.CodeSizeBytes;
            stage.ContinuationAddress = registered.ContinuationAddress;
            stage.ContinuationSizeBytes = registered.ContinuationSizeBytes;
            stage.UserDataAddress = registered.UserDataAddress;
            stage.InputSemanticsAddress = registered.InputSemanticsAddress;
            stage.InputSemanticsCount = registered.InputSemanticsCount;
            stage.ScratchDwords = registered.ScratchDwords;
            stage.File = WriteBytes($"{label}-0x{registered.CodeAddress:X}.bin", registered.CodeAddress, registered.CodeSizeBytes);
            if (registered.IsFused)
            {
                stage.ContinuationHeaderAddress = Agc.AgcExports.GetShaderHeaderAddress(registered.ContinuationAddress);
                AddRange($"{label}-continuation-header", stage.ContinuationHeaderAddress, 0x100);
                stage.ContinuationFile = WriteBytes(
                    $"{label}-continuation-0x{registered.ContinuationAddress:X}.bin", registered.ContinuationAddress, registered.ContinuationSizeBytes);
            }

            // The program bytes and the tables must be readable at their own addresses on replay.
            AddRange($"{label}-code", registered.CodeAddress, registered.CodeSizeBytes);
            if (registered.IsFused)
            {
                AddRange($"{label}-continuation", registered.ContinuationAddress, registered.ContinuationSizeBytes);
            }

            WorkCapture.AddShaderMetadata(this, source);
        }

        // Whatever preparing this program read, wherever its user scalars pointed.
        foreach (var (address, size) in WorkCapture.ShaderReads(codeAddress))
        {
            AddRange($"{label}-program-read", address, size);
        }

        AddDescriptorRanges(label, program, resources);
        Manifest.Stages.Add(stage);
    }

    // The buffers and images the descriptors point at; the images are bounded, the buffers exact.
    private void AddDescriptorRanges(string label, ShaderProgramInfo program, ShaderStageResources resources)
    {
        var buffers = resources.Resources.Buffers;
        for (var index = 0; index < buffers.Length; index++)
        {
            if (buffers[index].Length < 4)
            {
                continue;
            }

            var descriptor = BufferDescriptorWords.From(buffers[index]);
            var footprint = descriptor.Footprint() ?? 0;
            if (footprint == 0)
            {
                continue;
            }

            AddRange($"{label}-buffer{index}", descriptor.Address, footprint);
        }

        var images = resources.Resources.Images;
        for (var index = 0; index < images.Length; index++)
        {
            if (images[index].Length < 8)
            {
                continue;
            }

            var words = new Images.TextureDescriptorWords(images[index]);
            if (words.IsNull)
            {
                continue;
            }

            var size = ImageByteCount(words);
            if (size > WorkCapture.ImageCap)
            {
                Note($"the {label} image {index} at 0x{words.BaseAddress:X} was cut to 0x{WorkCapture.ImageCap:X} of 0x{size:X} bytes");
                size = WorkCapture.ImageCap;
            }

            AddRange($"{label}-image{index}", words.BaseAddress, size);
            if (words.MetadataAddress != 0)
            {
                AddRange($"{label}-image{index}-metadata", words.MetadataAddress, PageBytes);
            }
        }

        foreach (var range in resources.Resources.DeviceAddressRanges)
        {
            AddRange($"{label}-device-address{range.Handle}", range.Base, Math.Min(range.Size, WorkCapture.ImageCap));
        }

        if (program.Bindings is { UsesNggBuffers: true })
        {
            Note($"the {label} stage reads emulated geometry record buffers, which live on the device and are not part of the case");
        }
    }

    // The exact size when the request builder accepts the descriptor, else a bounded guess.
    private ulong ImageByteCount(in Images.TextureDescriptorWords words)
    {
        try
        {
            var shape = new Images.ShaderImageShape(false, false, false, false, Images.TextureNumericClass.Float);
            var resolution = Images.ImageRequestBuilders.Texture(words.Fields, shape);
            var size = resolution.Request.Description.Data.Size;
            if (size != 0)
            {
                return size;
            }
        }
        catch (Exception)
        {
            // The descriptor is not one the builder models; fall back to the cap.
        }

        Note($"the image at 0x{words.BaseAddress:X} has no modeled size; 0x{WorkCapture.ImageCap:X} bytes were taken");
        return WorkCapture.ImageCap;
    }

    // The guest ranges the resolvers read for this draw or dispatch: the vertex attribute and buffer
    // tables, the input semantics and the fetch data, wherever the user scalars pointed them.
    public void AddResolutionReads()
    {
        var count = 0;
        foreach (var (address, size) in WorkCapture.ResolutionReads())
        {
            AddRange("resolve-read", address, size);
            count++;
        }

        if (count == 0)
        {
            Note("no resolution reads were recorded; a case may be missing the tables the program resolves through");
        }
    }

    // Downloads a bound host image; the file appears once the queued copy completes.
    public void CaptureImage(string phase, string role, ulong address)
    {
        if (address == 0 || WorkCapture.ImageDownload is not { } download)
        {
            return;
        }

        var file = $"{phase}-{role}-0x{address:X}.bin";
        System.IO.Directory.CreateDirectory(Directory);
        if (download(address, Path.Combine(Directory, file)) is not { } image)
        {
            Note($"the {phase} contents of {role} at 0x{address:X} could not be downloaded");
            return;
        }

        Manifest.Images.Add(new WorkCaseImage
        {
            Phase = phase,
            Role = role,
            Address = address,
            File = file,
            Format = image.Format,
            Width = image.Width,
            Height = image.Height,
            Depth = image.Depth,
            Layers = image.Layers,
            TexelBytes = image.TexelBytes,
        });
    }

    // Writes the merged ranges and the manifest; called once the work was recorded.
    public void Complete()
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Smallest ranges first: programs, headers and constant data are a few KiB and a case is
        // useless without them, while a 4K render target alone can spend most of the budget.
        foreach (var (role, address, size) in Merge().OrderBy(static range => range.Item3))
        {
            if (Manifest.CapturedBytes >= _budget)
            {
                Note($"the range {role} at 0x{address:X} was dropped: the 0x{_budget:X} byte budget is spent");
                continue;
            }

            var bounded = Math.Min(size, _budget - Manifest.CapturedBytes);
            if (bounded != size)
            {
                Note($"the range {role} at 0x{address:X} was cut to 0x{bounded:X} of 0x{size:X} bytes by the budget");
            }

            var file = $"mem-0x{address:X}-0x{bounded:X}.bin";
            var written = WriteRange(file, address, bounded, out var readable);
            if (written == 0)
            {
                Note($"the range {role} at 0x{address:X} is unreadable");
                continue;
            }

            if (readable != bounded)
            {
                Note($"the range {role} at 0x{address:X} is mapped for 0x{readable:X} of 0x{bounded:X} bytes");
            }

            Manifest.Memory.Add(new WorkCaseRange { Role = role, Address = address, Size = written, File = file, RequestedSize = size });
            Manifest.CapturedBytes += written;
        }

        File.WriteAllText(Path.Combine(Directory, WorkCase.ManifestName), JsonSerializer.Serialize(Manifest, WorkCase.Json));
        Console.Error.WriteLine(
            $"[GPU][INFO] Work capture wrote '{Path.GetFullPath(Directory)}': kind={Manifest.Kind} selector={Manifest.Selector} " +
            $"stages={Manifest.Stages.Count} ranges={Manifest.Memory.Count} bytes=0x{Manifest.CapturedBytes:X} notes={Manifest.Truncated.Count}.");
        foreach (var note in Manifest.Truncated)
        {
            Console.Error.WriteLine($"[GPU][INFO]   truncated: {note}");
        }
    }

    // Overlapping and adjacent requests become one range, so a page is stored once.
    private List<(string Role, ulong Address, ulong Size)> Merge()
    {
        var merged = new List<(string Role, ulong Address, ulong Size)>();
        foreach (var request in _requested.OrderBy(static entry => entry.Address))
        {
            var end = request.Address + request.Size;
            if (merged.Count != 0)
            {
                var last = merged[^1];
                var lastEnd = last.Address + last.Size;
                if (request.Address <= lastEnd)
                {
                    merged[^1] = (last.Role == request.Role ? last.Role : $"{last.Role}+{request.Role}",
                        last.Address, Math.Max(lastEnd, end) - last.Address);
                    continue;
                }
            }

            merged.Add(request);
        }

        return merged;
    }

    private string WriteBytes(string file, ulong address, uint size)
    {
        return WriteRange(file, address, size, out _) == 0 ? string.Empty : file;
    }

    // Reads page by page and stops at the first unmapped one, so a partial range is still usable.
    private ulong WriteRange(string file, ulong address, ulong size, out ulong readable)
    {
        var bytes = new byte[checked((int)size)];
        readable = 0;
        while (readable < size)
        {
            var chunk = (int)Math.Min(PageBytes - ((address + readable) & (PageBytes - 1)), size - readable);
            if (!_readGuest(address + readable, bytes.AsSpan((int)readable, chunk)))
            {
                break;
            }

            readable += (ulong)chunk;
        }

        if (readable == 0)
        {
            return 0;
        }

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, file), bytes.AsSpan(0, (int)readable).ToArray());
        return readable;
    }
}
