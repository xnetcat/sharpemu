// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ExtendedUserDataTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint GsUserDataRegister = 0x8C;
    private const uint GsPgmRsrc2Register = GsUserDataRegister - 1;
    private const uint GsExtendedUserDataAddressLowRegister = 0x82;
    private const uint GsExtendedUserDataAddressHighRegister = 0x83;

    [Fact]
    public void NggExtendedUserData_IsLoadedIntoScalarRegistersStartingAtS32()
    {
        const ulong headerAddress = ShaderAddress + 0x100;
        const ulong userDataAddress = ShaderAddress + 0x400;
        const ulong specialsAddress = ShaderAddress + 0x500;
        const ulong extendedDataAddress = ShaderAddress + 0x800;
        uint[] expected =
        [
            0x7529_15FC,
            0x0010_0080,
            0x0000_0001,
            0x0004_DFAC,
            0xC0ED_8800,
            0x0010_0074,
            0x0000_0169,
            0x0004_DFAC,
        ];

        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, ShaderAddress, 0xBF81_0000); // S_ENDPGM
        WriteUInt64(memory, headerAddress + 0x08, userDataAddress);
        WriteUInt64(memory, headerAddress + 0x28, specialsAddress);
        WriteUInt16(memory, userDataAddress + 0x28, (ushort)expected.Length);
        WriteUInt16(memory, specialsAddress + 0x14, 0);
        WriteUInt16(memory, specialsAddress + 0x16, 0x1E);
        for (var index = 0; index < expected.Length; index++)
        {
            WriteUInt32(
                memory,
                extendedDataAddress + (ulong)(index * sizeof(uint)),
                expected[index]);
        }

        var shaderRegisters = new Dictionary<uint, uint>
        {
            [GsPgmRsrc2Register] = 12u << 1,
            [GsExtendedUserDataAddressLowRegister] =
                unchecked((uint)extendedDataAddress),
            [GsExtendedUserDataAddressHighRegister] =
                (uint)(extendedDataAddress >> 32),
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                shaderRegisters,
                GsUserDataRegister,
                out var state,
                out var error,
                userDataScalarRegisterBase: 8),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out error),
            error);

        Assert.Equal(expected, evaluation.InitialScalarRegisters.Skip(32).Take(8));
        Assert.Equal(0u, evaluation.InitialScalarRegisters[31]);
        Assert.Equal(0u, evaluation.InitialScalarRegisters[40]);
        Assert.Equal(0u, state.Metadata!.ExtendedUserDataRangeStart);
        Assert.Equal(0x1Eu, state.Metadata.ExtendedUserDataRangeEnd);
    }

    private static void WriteUInt16(
        FakeCpuMemory memory,
        ulong address,
        ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt32(
        FakeCpuMemory memory,
        ulong address,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(
        FakeCpuMemory memory,
        ulong address,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
