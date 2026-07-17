// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[Collection("SaveDataMemoryState")]
public sealed class SaveDataMetadataExportsTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong MountAddress = BaseAddress + 0x100;
    private const ulong MountResultAddress = BaseAddress + 0x200;
    private const ulong DirectoryNameAddress = BaseAddress + 0x300;
    private const ulong ParameterAddress = BaseAddress + 0x400;
    private const ulong IconAddress = BaseAddress + 0x500;
    private const ulong IconDataAddress = BaseAddress + 0x600;
    private const int UserId = 0x1001;
    private const string TitleId = "METADATATEST";
    private const string DirectoryName = "slot0";

    private readonly FakeCpuMemory _memory = new(BaseAddress, 0x2000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataMetadataExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-savedata-metadata-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
    }

    private string SavePath =>
        Path.Combine(_root, UserId.ToString(), TitleId, DirectoryName);

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SetParam_Title_PersistsSearchMetadata()
    {
        Assert.Equal(0, Mount());
        var title = Encoding.ASCII.GetBytes("Silent Hill\0");
        Assert.True(_memory.TryWrite(ParameterAddress, title));
        _ctx[CpuRegister.Rdi] = MountResultAddress;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = ParameterAddress;
        _ctx[CpuRegister.Rcx] = (ulong)title.Length;

        var result = SaveDataExports.SaveDataSetParam(_ctx);

        Assert.Equal(0, result);
        var metadata = File.ReadAllBytes(
            Path.Combine(SavePath, "sce_sys", "sharpemu-param.bin"));
        Assert.Equal("Silent Hill", ReadAscii(metadata.AsSpan(0, 128)));
        Assert.NotEqual(
            0,
            BinaryPrimitives.ReadInt64LittleEndian(
                metadata.AsSpan(0x508, sizeof(long))));
    }

    [Fact]
    public void SaveIcon_WritesMountedSaveIcon()
    {
        Assert.Equal(0, Mount());
        byte[] icon = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(_memory.TryWrite(IconDataAddress, icon));
        WriteUInt64(IconAddress + 0x00, IconDataAddress);
        WriteUInt64(IconAddress + 0x08, (ulong)icon.Length);
        WriteUInt64(IconAddress + 0x10, (ulong)icon.Length);
        _ctx[CpuRegister.Rdi] = MountResultAddress;
        _ctx[CpuRegister.Rsi] = IconAddress;

        var result = SaveDataExports.SaveDataSaveIcon(_ctx);

        Assert.Equal(0, result);
        Assert.Equal(
            icon,
            File.ReadAllBytes(Path.Combine(SavePath, "sce_sys", "icon0.png")));
    }

    private int Mount()
    {
        _memory.WriteCString(DirectoryNameAddress, DirectoryName);
        WriteInt32(MountAddress + 0x00, UserId);
        WriteUInt64(MountAddress + 0x08, DirectoryNameAddress);
        WriteUInt64(MountAddress + 0x10, 96);
        WriteUInt64(MountAddress + 0x18, 0);
        WriteUInt32(MountAddress + 0x20, 1u << 5);
        WriteUInt32(MountAddress + 0x24, 0);
        WriteUInt32(MountAddress + 0x28, 0);
        _ctx[CpuRegister.Rdi] = MountAddress;
        _ctx[CpuRegister.Rsi] = MountResultAddress;
        return SaveDataExports.SaveDataMount3(_ctx);
    }

    private void WriteInt32(ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? bytes : bytes[..length]);
    }
}
