// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Finds embedded RDNA2 shader instruction streams in a decrypted game dump
/// before the game is launched. This is necessarily heuristic: many games
/// generate or upload shaders at runtime, and ordinary x86 data can resemble
/// an AMD instruction. A stream is only accepted after it reaches S_ENDPGM;
/// decoder errors are reported only after a useful run of valid instructions.
/// </summary>
public static class Gen5ShaderPreflightScanner
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int LookAheadBytes = 16;
    // Four words occur by chance often enough in x86 code and compressed game
    // data to be noisy. Sixteen is still well before the first unsupported
    // opcode in normal shader entry blocks, while filtering those false paths.
    private const int MinimumInstructionsBeforeError = 16;
    private const int MaximumInstructionsPerCandidate = 512;
    private const int ReportLimit = 20;

    /// <summary>
    /// Scans the given eboot and nearby <c>sce_module</c> binaries without
    /// constructing an emulator runtime. Results use the runtime shader
    /// decoder's opcode and instruction-width tables.
    /// </summary>
    public static bool TryScan(string ebootPath, TextWriter output, out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ebootPath);
        ArgumentNullException.ThrowIfNull(output);

        error = string.Empty;
        if (!File.Exists(ebootPath))
        {
            error = $"File was not found: {ebootPath}";
            return false;
        }

        var result = new ScanResult();
        try
        {
            foreach (var file in EnumerateInputFiles(ebootPath))
            {
                ScanFile(file, result);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }

        WriteReport(output, result);
        return true;
    }

    private static IEnumerable<string> EnumerateInputFiles(string ebootPath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(ebootPath),
        };
        var moduleDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(ebootPath))!);
        for (var level = 0; directory is not null && level < 4; level++, directory = directory.Parent)
        {
            var moduleDirectory = Path.Combine(directory.FullName, "sce_module");
            if (Directory.Exists(moduleDirectory))
            {
                moduleDirectories.Add(moduleDirectory);
            }
        }

        foreach (var moduleDirectory in moduleDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(moduleDirectory, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".sprx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".prx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".elf", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".self", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }
        }

        return files.OrderBy(static file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static void ScanFile(string path, ScanResult result)
    {
        var bytes = new byte[BufferSize + LookAheadBytes];
        using var stream = File.OpenRead(path);
        var carry = 0;
        long fileOffset = 0;
        while (true)
        {
            var read = stream.Read(bytes, carry, BufferSize);
            if (read == 0)
            {
                break;
            }

            var available = carry + read;
            var isFinalChunk = stream.Position == stream.Length;
            var safeLength = isFinalChunk ? available : Math.Max(0, available - LookAheadBytes);
            ScanBuffer(path, bytes.AsSpan(0, available), fileOffset - carry, safeLength, result);
            result.BytesScanned += read;

            carry = Math.Min(LookAheadBytes, available);
            bytes.AsSpan(available - carry, carry).CopyTo(bytes);
            fileOffset += read;
        }

        result.FilesScanned++;
    }

    private static void ScanBuffer(
        string path,
        ReadOnlySpan<byte> bytes,
        long bufferFileOffset,
        int safeLength,
        ScanResult result)
    {
        var memory = new BufferMemory(bytes.ToArray());
        var context = new CpuContext(memory, Generation.Gen5);
        for (var start = 0; start + sizeof(uint) <= safeLength; start++)
        {
            if (((bufferFileOffset + start) & 3) != 0)
            {
                continue;
            }

            var cursor = start;
            var instructionCount = 0;
            var sawTraceMarker = false;
            var sawPrefetchMarker = false;
            Dictionary<string, int>? decodedOpcodes = null;
            while (cursor + sizeof(uint) <= bytes.Length && instructionCount < MaximumInstructionsPerCandidate)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, sizeof(uint)));
                if (!Gen5ShaderTranslator.TryDecodeInstructionForPreflight(
                        context,
                        (uint)cursor,
                        word,
                        out var opcode,
                        out var sizeDwords,
                        out var decodeError))
                {
                    if (instructionCount >= MinimumInstructionsBeforeError &&
                        sawTraceMarker &&
                        sawPrefetchMarker &&
                        decodeError.StartsWith("unknown-", StringComparison.Ordinal))
                    {
                        result.AddDecoderError(NormalizeError(decodeError), path, bufferFileOffset + cursor);
                    }

                    break;
                }

                var instructionBytes = checked((int)sizeDwords * sizeof(uint));
                if (instructionBytes <= 0 || cursor + instructionBytes > bytes.Length)
                {
                    break;
                }

                decodedOpcodes ??= new Dictionary<string, int>(StringComparer.Ordinal);
                decodedOpcodes.TryGetValue(opcode, out var count);
                decodedOpcodes[opcode] = count + 1;
                // Sony's Gen5 compiler emits these two scalar markers in the
                // entry prologue. Requiring both keeps arbitrary x86/data from
                // being reported as tens of thousands of missing shader
                // opcodes while retaining every SDK shader observed at runtime.
                if (instructionCount < 8)
                {
                    sawTraceMarker |= opcode == "STtraceData";
                    sawPrefetchMarker |= opcode == "SInstPrefetch";
                }
                instructionCount++;
                cursor += instructionBytes;

                if (opcode == "SEndpgm")
                {
                    if (instructionCount >= MinimumInstructionsBeforeError &&
                        sawTraceMarker &&
                        sawPrefetchMarker)
                    {
                        result.CompletedShaderStreams++;
                        result.AddOpcodes(decodedOpcodes);
                        // The remainder of this decoded shader cannot be an
                        // independent stream start, so avoid re-decoding it.
                        start = cursor - 1;
                    }

                    break;
                }
            }
        }
    }

    private static string NormalizeError(string error)
    {
        var pcIndex = error.IndexOf(" pc=", StringComparison.Ordinal);
        if (pcIndex >= 0)
        {
            error = error[..pcIndex];
        }

        var wordIndex = error.IndexOf(" word=", StringComparison.Ordinal);
        return wordIndex >= 0 ? error[..wordIndex] : error;
    }

    private static void WriteReport(TextWriter output, ScanResult result)
    {
        output.WriteLine(
            $"[OPCODE-SCAN] files={result.FilesScanned} bytes={result.BytesScanned:N0} " +
            $"complete_shader_streams={result.CompletedShaderStreams} " +
            $"decoder_error_sites={result.DecoderErrorSites} " +
            $"distinct_decoder_errors={result.DecoderErrors.Count}");

        WriteCounts(output, "decoder errors", result.DecoderErrors);
        WriteCounts(output, "decoded opcodes", result.DecodedOpcodes);
        output.WriteLine(
            "[OPCODE-SCAN] Note: this only sees RDNA2 bytecode embedded in decrypted files; " +
            "runtime-generated or streamed shaders still require compatibility logging during play.");
    }

    private static void WriteCounts(TextWriter output, string title, Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            output.WriteLine($"[OPCODE-SCAN] {title}: none");
            return;
        }

        output.WriteLine($"[OPCODE-SCAN] {title} (top {ReportLimit}):");
        foreach (var pair in counts
                     .OrderByDescending(static pair => pair.Value)
                     .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                     .Take(ReportLimit))
        {
            output.WriteLine($"  {pair.Value,6}  {pair.Key}");
        }
    }

    private sealed class BufferMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress > (ulong)bytes.Length ||
                destination.Length > bytes.Length - (int)virtualAddress)
            {
                return false;
            }

            bytes.AsSpan((int)virtualAddress, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private sealed class ScanResult
    {
        public long BytesScanned { get; set; }
        public int FilesScanned { get; set; }
        public int CompletedShaderStreams { get; set; }
        public int DecoderErrorSites { get; private set; }
        public Dictionary<string, int> DecoderErrors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> DecodedOpcodes { get; } = new(StringComparer.Ordinal);

        public void AddDecoderError(string error, string path, long offset)
        {
            DecoderErrorSites++;
            DecoderErrors.TryGetValue(error, out var count);
            DecoderErrors[error] = count + 1;
        }

        public void AddOpcodes(Dictionary<string, int> opcodes)
        {
            foreach (var pair in opcodes)
            {
                DecodedOpcodes.TryGetValue(pair.Key, out var count);
                DecodedOpcodes[pair.Key] = count + pair.Value;
            }
        }
    }
}
