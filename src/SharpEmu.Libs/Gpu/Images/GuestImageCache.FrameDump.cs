// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Writes every uncompressed color image that one frame used, as linear mip 0 bytes, to
// SHARPEMU_DUMP_FRAME_DIR (default ./frame-dump): at the first flip after SHARPEMU_DUMP_FRAME_AT
// seconds, and at each flip after the file named by SHARPEMU_DUMP_FRAME_TRIGGER appears (the file
// is removed). Each file name carries the guest address, the host format and the extent, so a
// wrong pass can be found by looking at its inputs and outputs instead of the final image only.
public sealed unsafe partial class GuestImageCache
{
    private static readonly double FrameDumpAt =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FRAME_AT"), out var seconds) ? seconds : -1;

    private static readonly string? FrameDumpTrigger = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FRAME_TRIGGER");
    private static readonly long FrameDumpStart = Stopwatch.GetTimestamp();
    private static int _frameDumped;
    private static int _frameDumpCount;
    private long _frameDumpTriggerCheck;
    private ulong _previousFlipTick;
    private string? _frameDumpDirectory;

    // Called under the cache lock when the display surface of a flip is looked up.
    private void DumpFrameImagesIfRequested()
    {
        var frameStart = _previousFlipTick;
        _previousFlipTick = _scheduler.CurrentTick;
        // A request first records the commands of the next frame, then dumps what that frame used.
        if (_frameDumpDirectory is null)
        {
            if (IsFrameDumpRequested())
            {
                _frameDumpDirectory = Path.Combine(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FRAME_DIR") ?? "frame-dump",
                    $"frame-{Interlocked.Increment(ref _frameDumpCount):D2}");
                Rendering.FrameCommandLog.Start(_frameDumpDirectory);
                StartTargetSteps(_frameDumpDirectory);
            }

            return;
        }

        var directory = _frameDumpDirectory;
        _frameDumpDirectory = null;
        Rendering.FrameCommandLog.Write("Flip");
        Rendering.FrameCommandLog.TargetStepDump = null;
        Rendering.FrameCommandLog.Stop();
        Directory.CreateDirectory(directory);
        var sequence = 0;
        var dumped = 0;
        _slots.ForEach((_, image) =>
        {
            if (!image.Registered || !image.Backing.Exists || image.DepthOwner.IsValid || image.LastAccessTick < frameStart)
            {
                return;
            }

            if (TryDumpImage(image, directory, sequence++))
            {
                dumped++;
            }
        });
        Console.Error.WriteLine(
            $"[GPU][INFO] Frame dump: {dumped} images queued to '{Path.GetFullPath(directory)}' for ticks {frameStart}..{_previousFlipTick}.");
    }

    private static readonly string? TargetStepsFile = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TARGET_STEPS_FILE");

    // Reads the color target addresses to snapshot after each draw of this frame (hex, one per line).
    private void StartTargetSteps(string directory)
    {
        if (TargetStepsFile is null || !File.Exists(TargetStepsFile))
        {
            return;
        }

        var addresses = new HashSet<ulong>();
        foreach (var line in File.ReadAllLines(TargetStepsFile))
        {
            var text = line.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var address))
            {
                addresses.Add(address);
            }
        }

        if (addresses.Count == 0)
        {
            return;
        }

        var stepDirectory = Path.Combine(directory, "steps");
        Directory.CreateDirectory(stepDirectory);
        Rendering.FrameCommandLog.TargetStepDump = (address, sequence) =>
        {
            if (!addresses.Contains(address))
            {
                return;
            }

            using var held = _lock.Hold();
            foreach (var imageIdentifier in FindImagesInRange(address, 1, pageOverlap: false))
            {
                var image = _slots[imageIdentifier];
                if (image.Description.Data.Address == address && image.Backing.Exists && !image.DepthOwner.IsValid)
                {
                    TryDumpImage(image, stepDirectory, checked((int)sequence));
                }
            }
        };
    }

    private bool IsFrameDumpRequested()
    {
        if (FrameDumpAt >= 0 && Stopwatch.GetElapsedTime(FrameDumpStart).TotalSeconds >= FrameDumpAt &&
            Interlocked.Exchange(ref _frameDumped, 1) == 0)
        {
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        if (FrameDumpTrigger is null || now < _frameDumpTriggerCheck)
        {
            return false;
        }

        _frameDumpTriggerCheck = now + Stopwatch.Frequency / 2;
        if (!File.Exists(FrameDumpTrigger))
        {
            return false;
        }

        File.Delete(FrameDumpTrigger);
        return true;
    }

    private bool TryDumpImage(CachedImage image, string directory, int sequence)
    {
        var description = image.Description;
        var format = image.Backing.Format;
        var texelBytes = FrameDumpTexelBytes(format);
        var extent = description.Extent;
        var layers = image.Backing.ImageType == ImageType.Type3D ? 1u : Math.Max(image.Backing.Layers, 1u);
        var name = $"{sequence:D4}-0x{description.Data.Address:X}-{format}-{extent.Width}x{extent.Height}x{Math.Max(extent.Depth, 1)}x{layers}" +
            $"{(image.IsGpuModified ? "-gpu" : string.Empty)}";
        if (texelBytes == 0)
        {
            Console.Error.WriteLine($"[GPU][INFO] Frame dump skipped {name}: the format has no dump size.");
            return false;
        }

        var size = (ulong)extent.Width * extent.Height * Math.Max(extent.Depth, 1) * layers * texelBytes;
        var buffer = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, size);
        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, layers),
            ImageExtent = new Extent3D(extent.Width, extent.Height, Math.Max(extent.Depth, 1)),
        };
        _scheduler.EndRendering();
        image.DownloadToBuffer([copy], buffer.Handle, 0, size);
        var path = Path.Combine(directory, name + ".raw");
        var guestBytes = new byte[description.Data.Size];
        if (_backing.TryReadBacking(description.Data.Address, guestBytes))
        {
            File.WriteAllBytes(Path.Combine(directory, name + ".guest"), guestBytes);
        }

        _scheduler.QueuePriorityCompletionAction(() =>
        {
            buffer.Invalidate(0, size);
            File.WriteAllBytes(path, buffer.Mapped[..checked((int)size)].ToArray());
            buffer.Dispose();
        });
        return true;
    }

    private static uint FrameDumpTexelBytes(Format format) => format switch
    {
        Format.R8Unorm or Format.R8Uint => 1,
        Format.R8G8Unorm or Format.R16Sfloat or Format.R16Unorm or Format.R16Uint => 2,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or
            Format.A2B10G10R10UnormPack32 or Format.A2R10G10B10UnormPack32 or Format.B10G11R11UfloatPack32 or
            Format.R32Sfloat or Format.R32Uint or Format.R16G16Sfloat or Format.R16G16Unorm or Format.R8G8B8A8Uint => 4,
        Format.R16G16B16A16Sfloat or Format.R16G16B16A16Unorm or Format.R32G32Sfloat => 8,
        Format.R32G32B32A32Sfloat => 16,
        _ => 0,
    };
}
