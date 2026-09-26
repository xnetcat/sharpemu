// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace SharpEmu.Libs.Tests.Gpu.Replay;

// Writes an 8-bit RGBA PNG. A replayed target is read as raw bytes; this is only so the result can
// be looked at without a converter, so it takes the one layout the render targets come back in.
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // True when the format is one this writer can lay out; other formats stay raw.
    public static bool Supports(string format) => format is
        "R8G8B8A8Unorm" or "R8G8B8A8Srgb" or "R8G8B8A8Uint" or "B8G8R8A8Unorm" or "B8G8R8A8Srgb";

    private static bool IsBgra(string format) => format.StartsWith("B8G8R8A8", StringComparison.Ordinal);

    public static void Write(string path, ReadOnlySpan<byte> texels, uint width, uint height, string format)
    {
        var bgra = IsBgra(format);
        var rows = new byte[(int)height * ((int)width * 4 + 1)];
        for (var y = 0; y < (int)height; y++)
        {
            var rowStart = y * ((int)width * 4 + 1);
            rows[rowStart] = 0;
            for (var x = 0; x < (int)width; x++)
            {
                var source = (y * (int)width + x) * 4;
                var target = rowStart + 1 + x * 4;
                if (source + 3 >= texels.Length)
                {
                    break;
                }

                rows[target + 0] = bgra ? texels[source + 2] : texels[source + 0];
                rows[target + 1] = texels[source + 1];
                rows[target + 2] = bgra ? texels[source + 0] : texels[source + 2];
                rows[target + 3] = texels[source + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(rows);
        }

        using var file = File.Create(path);
        file.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream file, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        file.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        file.Write(checksum);
    }
}
