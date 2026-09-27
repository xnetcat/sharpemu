// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Rendering;

// Reads guest bytes for the capture; the host supplies it so the capture has no memory of its own.
internal delegate bool WorkCaptureGuestReader(ulong address, Span<byte> destination);

// The extent and format of one host image the capture downloaded; the replay reads the raw file with it.
public readonly record struct CapturedImageBytes(string Format, uint Width, uint Height, uint Depth, uint Layers, uint TexelBytes);

// Writes one draw or dispatch, with everything it read, to a case directory the replay tool re-runs
// offline. A wrong pass is then debugged from a checked-in case instead of by launching the game
// again and waiting for the frame that shows it.
//
//   SHARPEMU_CAPTURE_WORK=<selector>[,<selector>...]  a program hash (0x... as the logs print it) or
//                                                     a shader code address, with an optional @N for
//                                                     the Nth match (the first by default)
//   SHARPEMU_CAPTURE_DIR=<dir>                        where the case directories go (./work-capture)
//   SHARPEMU_CAPTURE_LIMIT=<count>                    how many cases one run may write (8)
//   SHARPEMU_CAPTURE_MAX_BYTES=<bytes>                the guest-memory budget of one case (512 MiB: a 4K MRT base pass needs ~0x30000000)
//
// Every check behind the enabled flag is a static readonly read, so a run without the variable set
// does no work at all.
internal static class WorkCapture
{
    private const ulong DefaultByteBudget = 512UL * 1024 * 1024;
    private const ulong PerImageByteCap = 128UL * 1024 * 1024;

    // What the header chains carry beyond their own words: the user data block and the direct
    // resource offsets it points at, which the vertex tables read while the program resolves.
    private const ulong HeaderBytes = 0x100;
    private const ulong UserDataBlockBytes = 0x200;
    private const ulong DirectResourceOffsetBytes = 0x80;
    private const ulong DirectResourceOffsetField = 0x00;

    // The order matters: the selector list reports the other three when it parses.
    private static readonly string Root = Environment.GetEnvironmentVariable("SHARPEMU_CAPTURE_DIR") ?? "work-capture";
    private static readonly int Limit = ParseCount(Environment.GetEnvironmentVariable("SHARPEMU_CAPTURE_LIMIT"), 8);
    private static readonly ulong ByteBudget = ParseSize(Environment.GetEnvironmentVariable("SHARPEMU_CAPTURE_MAX_BYTES"), DefaultByteBudget);
    private static readonly WorkCaptureSelector[] Selectors = ParseSelectors(Environment.GetEnvironmentVariable("SHARPEMU_CAPTURE_WORK"));
    private static readonly ConcurrentDictionary<ulong, ShaderSource> NotedShaders = new();
    private static int _written;

    // True once a selector was given; nothing else in the capture runs while this is false.
    public static bool Enabled { get; } = Selectors.Length != 0;

    // Downloads the host image registered at a guest address into a file; the image cache installs it.
    public static Func<ulong, string, CapturedImageBytes?>? ImageDownload { get; set; }

    // The pipeline cache notes every header it resolved, so the case carries the program bytes and
    // the tables the replay must re-read at the same addresses.
    public static void NoteShader(ShaderSource source) => NotedShaders[source.Address] = source;

    // Opens a case when one of the keys matches a selector that still wants a match.
    public static WorkCaptureBuilder? TryBegin(WorkCaseKind kind, ulong submitId, WorkCaptureGuestReader readGuest, params ulong[] keys)
    {
        if (!Enabled || Volatile.Read(ref _written) >= Limit)
        {
            return null;
        }

        foreach (var selector in Selectors)
        {
            if (!keys.Contains(selector.Key))
            {
                continue;
            }

            var occurrence = selector.Seen();
            if (occurrence != selector.Occurrence)
            {
                continue;
            }

            if (Interlocked.Increment(ref _written) > Limit)
            {
                return null;
            }

            return new WorkCaptureBuilder(kind, submitId, selector, readGuest, Root, ByteBudget);
        }

        return null;
    }

    public static ShaderSource? NotedShader(ulong codeAddress) =>
        NotedShaders.TryGetValue(codeAddress, out var source) ? source : null;

    // The header pages, the user data block and the direct resource offsets of one program.
    public static void AddShaderMetadata(WorkCaptureBuilder builder, ShaderSource source)
    {
        var registered = source.Registered;
        builder.AddRange($"{source.Label}-header", registered.HeaderAddress, HeaderBytes);
        if (registered.UserDataAddress != 0)
        {
            builder.AddRange($"{source.Label}-user-data", registered.UserDataAddress, UserDataBlockBytes);
            if (builder.TryReadUInt64(registered.UserDataAddress + DirectResourceOffsetField, out var offsets) && offsets != 0)
            {
                builder.AddRange($"{source.Label}-direct-resources", offsets, DirectResourceOffsetBytes);
            }
        }

        if (registered.InputSemanticsAddress != 0)
        {
            builder.AddRange($"{source.Label}-input-semantics", registered.InputSemanticsAddress,
                Math.Max((ulong)registered.InputSemanticsCount * sizeof(uint), 0x40));
        }
    }

    public static ulong ImageCap => PerImageByteCap;

    private static int ParseCount(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static ulong ParseSize(string? text, ulong fallback)
    {
        if (text is null)
        {
            return fallback;
        }

        // One range is read into a single array, so the budget cannot exceed what one can hold.
        return TryParseUnsigned(text, out var value) && value != 0 ? Math.Min(value, int.MaxValue) : fallback;
    }

    private static bool TryParseUnsigned(string text, out ulong value)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // "<hash-or-address>[@occurrence]"; an unparsable selector is reported and dropped.
    private static WorkCaptureSelector[] ParseSelectors(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var selectors = new List<WorkCaptureSelector>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('@');
            var keyText = at < 0 ? part : part[..at];
            var occurrence = 1u;
            if (at >= 0 && (!uint.TryParse(part.AsSpan(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out occurrence) || occurrence == 0))
            {
                Console.Error.WriteLine($"[GPU][WARN] The work-capture selector has an invalid occurrence: selector={part}.");
                continue;
            }

            if (!TryParseUnsigned(keyText, out var key) || key == 0)
            {
                Console.Error.WriteLine($"[GPU][WARN] The work-capture selector is not a hash or address: selector={part}.");
                continue;
            }

            selectors.Add(new WorkCaptureSelector(part, key, occurrence));
        }

        if (selectors.Count != 0)
        {
            Console.Error.WriteLine(
                $"[GPU][INFO] Work capture armed: selectors=[{string.Join(' ', selectors.Select(static s => s.Text))}] " +
                $"dir='{Path.GetFullPath(Root)}' limit={Limit} budget=0x{ByteBudget:X}.");
        }

        return [.. selectors];
    }

}

// One armed selector and how many matches it has seen.
internal sealed class WorkCaptureSelector(string text, ulong key, uint occurrence)
{
    private uint _seen;

    public string Text => text;

    public ulong Key => key;

    public uint Occurrence => occurrence;

    public uint Seen() => Interlocked.Increment(ref _seen);
}
