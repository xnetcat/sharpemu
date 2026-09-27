// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;

namespace SharpEmu.Libs.Tests.Gpu.Replay;

// A case the test writes itself: one point drawn by two hand-assembled Gen5 programs into a 64x64
// linear target. It proves the round trip - manifest, headers, program bytes, banks, memory - without
// a game, so a broken case format fails here instead of in front of a captured frame.
internal static class SyntheticWorkCase
{
    public const uint Size = 64;

    // A texel the triangle covers, at the middle of the target.
    public const uint CenterX = Size / 2;
    public const uint CenterY = Size / 2;

    private const ulong VertexCode = 0x2_1000_0000;
    private const ulong VertexHeader = 0x2_1000_1000;
    private const ulong VertexUserData = 0x2_1000_2000;
    private const ulong PixelCode = 0x2_1001_0000;
    private const ulong PixelHeader = 0x2_1001_1000;
    private const ulong PixelUserData = 0x2_1001_2000;
    private const ulong ColorTarget = 0x2_1010_0000;
    private const ulong HeaderBytes = 0x60;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong UserDataOffset = 0x08;
    private const ulong InputSemanticsOffset = 0x30;
    private const ulong InputSemanticsCountOffset = 0x50;

    // The embedded vertex fetch: the header's user data block names two user scalar registers, those
    // registers hold pointers, and only following them reaches the attribute and buffer tables. The
    // two tables sit in granules of their own so a case can drop one and prove it is load bearing.
    private const ulong VertexDirectOffsets = 0x2_1000_2400;
    private const ulong VertexSemantics = 0x2_1000_2800;
    private const ulong VertexAttributeTable = 0x2_1004_0000;
    private const ulong VertexBufferTable = 0x2_1008_0000;
    private const ulong VertexStream = 0x2_100C_0000;
    private const int BufferTableRegister = 0;
    private const int AttributeTableRegister = 2;
    private const uint DirectResourceCount = 11;
    private const uint DirectResourceVertexBufferTable = 8;
    private const uint DirectResourceVertexAttributeTable = 10;
    private const ushort IllegalDirectOffset = 0xFFFF;
    private const uint VertexStride = 8;
    private const uint VertexCount = 3;
    private const uint Float2Format = 64;

    public const string AttributeTableRole = "vertex-attribute-table";

    // The fullscreen triangle of the vertex index: x = (index & 1) * 4 - 1, y = (index >> 1) * 4 - 1,
    // so the three vertices at (-1,-1), (3,-1) and (-1,3) cover the whole viewport. The translated
    // vertex stage puts the index in v5, not v0, which is where these two read it.
    private static readonly uint[] VertexProgram =
    [
        0x36020A81,             // v_and_b32 v1, 1, v5
        0x2C040A81,             // v_lshrrev_b32 v2, 1, v5
        0x7E020D01,             // v_cvt_f32_u32 v1, v1
        0x7E040D02,             // v_cvt_f32_u32 v2, v2
        0x100202F6,             // v_mul_f32 v1, 4.0, v1
        0x060202F3,             // v_add_f32 v1, -1.0, v1
        0x100404F6,             // v_mul_f32 v2, 4.0, v2
        0x060404F3,             // v_add_f32 v2, -1.0, v2
        0x7E060280,             // v_mov_b32 v3, 0
        0x7E0802F2,             // v_mov_b32 v4, 1.0
        0xF80008CF, 0x04030201, // exp pos0 v1, v2, v3, v4 done
        0xBF810000,             // s_endpgm
    ];

    // color = (1, 0, 0, 1). The add keeps the colour and stops the cache from recognising the pair
    // as a solid clear, which would replace the draw and prove nothing about the pipeline path.
    private static readonly uint[] PixelProgram =
    [
        0x7E0002F2,             // v_mov_b32 v0, 1.0
        0x7E020280,             // v_mov_b32 v1, 0
        0x06000300,             // v_add_f32 v0, v0, v1
        0xF800080F, 0x00010100, // exp mrt0 v0, v1, v1, v0 done
        0xBF810000,             // s_endpgm
    ];

    // Writes the case and returns its directory. With vertexTables the vertex stage fetches through
    // the pointer tables its user scalars name, which is the shape a real game's base pass has.
    public static string Write(string root, bool vertexTables = false)
    {
        Directory.CreateDirectory(root);
        var manifest = new WorkCaseManifest
        {
            Kind = WorkCaseKind.DrawAuto,
            Selector = "synthetic",
            Occurrence = 1,
            SubmitId = 1,
            CapturedUtc = DateTime.UtcNow.ToString("O"),
            Draw = new WorkCaseDraw
            {
                Count = 3,
                InstanceCount = 1,
                PrimitiveType = (uint)GuestPrimitiveType.TriangleList,
                Topology = "TriangleList",
                PacketCount = 3,
                PacketInstanceCount = 1,
                OffsetSource = nameof(DrawOffsetSource.Packet),
            },
            Banks = new WorkCaseBanks { Context = Context(), Shader = Shader(vertexTables), UserConfig = UserConfig() },
        };
        var vertexStage = Stage("Vertex", 0x1111_2222_3333_4444, VertexCode, VertexHeader, VertexUserData, VertexProgram, "vertex.bin");
        if (vertexTables)
        {
            vertexStage.InputSemanticsAddress = VertexSemantics;
            vertexStage.InputSemanticsCount = 1;
        }

        manifest.Stages.Add(vertexStage);
        manifest.Stages.Add(Stage("Pixel", 0x5555_6666_7777_8888, PixelCode, PixelHeader, PixelUserData, PixelProgram, "pixel.bin"));
        manifest.Targets.Add(new WorkCaseTarget
        {
            Role = "color0",
            Slot = 0,
            Address = ColorTarget,
            Width = Size,
            Height = Size,
            Samples = 1,
            Format = "R8G8B8A8Unorm",
        });

        File.WriteAllBytes(Path.Combine(root, "vertex.bin"), Bytes(VertexProgram));
        File.WriteAllBytes(Path.Combine(root, "pixel.bin"), Bytes(PixelProgram));
        AddRange(root, manifest, "vertex-code", VertexCode, Bytes(VertexProgram));
        AddRange(root, manifest, "vertex-header", VertexHeader,
            Header((uint)VertexProgram.Length * sizeof(uint), VertexUserData, vertexTables ? VertexSemantics : 0, vertexTables ? 1u : 0u));
        AddRange(root, manifest, "vertex-user-data", VertexUserData, vertexTables ? VertexUserDataBlock() : new byte[0x40]);
        if (vertexTables)
        {
            AddRange(root, manifest, "vertex-direct-offsets", VertexDirectOffsets, DirectOffsets());
            AddRange(root, manifest, "vertex-input-semantics", VertexSemantics, Words(2u << 16));
            AddRange(root, manifest, AttributeTableRole, VertexAttributeTable, Words(0u));
            AddRange(root, manifest, "vertex-buffer-table", VertexBufferTable, BufferTable());
            AddRange(root, manifest, "vertex-stream", VertexStream, Triangle());
        }

        AddRange(root, manifest, "pixel-code", PixelCode, Bytes(PixelProgram));
        AddRange(root, manifest, "pixel-header", PixelHeader, Header((uint)PixelProgram.Length * sizeof(uint), PixelUserData));
        AddRange(root, manifest, "pixel-user-data", PixelUserData, new byte[0x40]);
        AddRange(root, manifest, "color0", ColorTarget, new byte[Size * Size * 4]);
        File.WriteAllText(Path.Combine(root, WorkCase.ManifestName), JsonSerializer.Serialize(manifest, WorkCase.Json));
        return root;
    }

    // Adopts a replayed target as the case's "after" reference, so the next replay diffs against it.
    public static void AdoptReference(string caseDirectory, string replayedFile)
    {
        var manifest = WorkCase.Read(caseDirectory);
        const string file = "after-color0.bin";
        File.Copy(replayedFile, Path.Combine(caseDirectory, file), overwrite: true);
        manifest.Images.RemoveAll(static image => image.Phase == "after");
        manifest.Images.Add(new WorkCaseImage
        {
            Phase = "after",
            Role = "color0",
            Address = ColorTarget,
            File = file,
            Format = "R8G8B8A8Unorm",
            Width = Size,
            Height = Size,
            Depth = 1,
            Layers = 1,
            TexelBytes = 4,
        });
        File.WriteAllText(Path.Combine(caseDirectory, WorkCase.ManifestName), JsonSerializer.Serialize(manifest, WorkCase.Json));
    }

    private static WorkCaseStage Stage(string stage, ulong hash, ulong code, ulong header, ulong userData, uint[] program, string file) => new()
    {
        Stage = stage,
        Hash = hash,
        CodeAddress = code,
        HeaderAddress = header,
        CodeSizeBytes = (uint)program.Length * sizeof(uint),
        UserDataAddress = userData,
        File = file,
    };

    private static ContextRegisters Context()
    {
        var context = new ContextRegisters();
        context.ColorTargets[0] = RegisterWords.Color(ColorTarget, Size, Size);
        context.RenderTargetMask = 0xF;
        context.ShaderInterface.ColorShaderMask = 0xF;
        context.ScreenViewport.Viewports[0] = new ViewportRegisters
        {
            XScale = Size / 2f,
            XOffset = Size / 2f,
            YScale = Size / 2f,
            YOffset = Size / 2f,
            ZScale = 0.5f,
            ZOffset = 0.5f,
            MaxDepth = 1,
        };
        return context;
    }

    private static ShaderProgramRegisters Shader(bool vertexTables = false)
    {
        var shader = new ShaderProgramRegisters();
        shader.Vertex.ExportAddress = VertexCode;
        shader.Pixel.Address = PixelCode;
        if (vertexTables)
        {
            // The two table pointers, as a game's command stream writes them into the user scalars.
            var scalars = shader.Vertex.GeometryUserScalars;
            scalars.Set((uint)BufferTableRegister, unchecked((uint)VertexBufferTable), UserScalarKind.Unknown);
            scalars.Set((uint)BufferTableRegister + 1, (uint)(VertexBufferTable >> 32), UserScalarKind.Unknown);
            scalars.Set((uint)AttributeTableRegister, unchecked((uint)VertexAttributeTable), UserScalarKind.Unknown);
            scalars.Set((uint)AttributeTableRegister + 1, (uint)(VertexAttributeTable >> 32), UserScalarKind.Unknown);
        }

        return shader;
    }

    // The user data block: a pointer to the direct resource offsets and how many of them there are.
    private static byte[] VertexUserDataBlock()
    {
        var block = new byte[0x40];
        BinaryPrimitives.WriteUInt64LittleEndian(block, VertexDirectOffsets);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x2C), (ushort)DirectResourceCount);
        return block;
    }

    // One user scalar register per direct resource type; every type but the two tables is illegal.
    private static byte[] DirectOffsets()
    {
        var offsets = new byte[DirectResourceCount * sizeof(ushort)];
        for (var type = 0u; type < DirectResourceCount; type++)
        {
            var register = type switch
            {
                DirectResourceVertexBufferTable => (ushort)BufferTableRegister,
                DirectResourceVertexAttributeTable => (ushort)AttributeTableRegister,
                _ => IllegalDirectOffset,
            };
            BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan((int)type * sizeof(ushort)), register);
        }

        return offsets;
    }

    // One float2 vertex buffer descriptor, the layout the device gate already renders from.
    private static byte[] BufferTable() => Words(
        unchecked((uint)VertexStream),
        (uint)(VertexStream >> 32) | (VertexStride << 16),
        VertexCount,
        Float2Format << 12);

    private static byte[] Triangle() => Bytes(
    [
        0xBF800000, 0xBF800000, // (-1, -1)
        0x40400000, 0xBF800000, // ( 3, -1)
        0xBF800000, 0x40400000, // (-1,  3)
    ]);

    private static byte[] Words(params uint[] words) => Bytes(words);

    private static UserConfigRegisters UserConfig() => new() { PrimitiveType = (uint)GuestPrimitiveType.TriangleList };

    private static void AddRange(string root, WorkCaseManifest manifest, string role, ulong address, byte[] bytes)
    {
        var file = $"mem-0x{address:X}-0x{bytes.Length:X}.bin";
        File.WriteAllBytes(Path.Combine(root, file), bytes);
        manifest.Memory.Add(new WorkCaseRange
        {
            Role = role,
            Address = address,
            Size = (ulong)bytes.Length,
            File = file,
            RequestedSize = (ulong)bytes.Length,
        });
        manifest.CapturedBytes += (ulong)bytes.Length;
    }

    private static byte[] Header(uint codeSize, ulong userDataAddress, ulong inputSemanticsAddress = 0, uint inputSemanticsCount = 0)
    {
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan((int)ShaderSizeOffset), codeSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan((int)UserDataOffset), userDataAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan((int)InputSemanticsOffset), inputSemanticsAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan((int)InputSemanticsCountOffset), inputSemanticsCount);
        return header;
    }

    private static byte[] Bytes(uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        return bytes;
    }
}
