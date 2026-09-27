// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Runs the newly added VOP1/VOP3 opcodes on the GPU and checks bit-exact
/// results. spirv-val and "it compiles" cannot catch wrong semantics, and every
/// one of these has a hand-written bit sequence behind it, so the numbers are
/// the only real proof. No-ops with a note on hosts without a Metal device.
/// </summary>
public sealed class MslNewOpcodeExecutionTests(ITestOutputHelper output)
{
    private static readonly Gen5ComputeFixture NewOpcodes = new(
        "new-opcodes",
        [
            // ---- inputs ----
            0x7E0002FF, 0xAABBCCDD, // v_mov_b32 v0, 0xAABBCCDD  (perm S0, ffbh src)
            0x7E0202FF, 0x11223344, // v_mov_b32 v1, 0x11223344  (perm S1)
            0x7E0402FF, 0x0D0B0400, // v_mov_b32 v2, selectors 0x00/0x04/0x0B/0x0D
            0x7E0A02FF, 0x00003C00, // v_mov_b32 v5, 1.0h
            0x7E0C02FF, 0x00004000, // v_mov_b32 v6, 2.0h
            0x7E0E02FF, 0x00003800, // v_mov_b32 v7, 0.5h
            0x7E1002FF, 0xBEEF0000, // v_mov_b32 v8, min3_f16 destination sentinel
            0x7E1202FF, 0x0000FFFD, // v_mov_b32 v9, -3 as i16
            0x7E1402FF, 0x00000005, // v_mov_b32 v10, 5 as i16
            0x7E1602FF, 0x0000FFFF, // v_mov_b32 v11, -1 as i16
            0x7E1802FF, 0xCAFE0000, // v_mov_b32 v12, min3_i16 destination sentinel
            0x7E1C02FF, 0x00000004, // v_mov_b32 v14, shift count 4
            0x7E1E02FF, 0x00001234, // v_mov_b32 v15, shift value 0x1234
            0x7E1A02FF, 0xAAAA0000, // v_mov_b32 v13, lshlrev_b16 destination sentinel
            0x7E2202FF, 0x0123FFFE, // v_mov_b32 v17, {i16 291, i16 -2}
            0x7E2402FF, 0x41400000, // v_mov_b32 v18, 12.0f
            0x7E2802FF, 0x00004400, // v_mov_b32 v20, 4.0h
            0x7E2A02FF, 0xD00D0000, // v_mov_b32 v21, log_f16 destination sentinel

            // ---- the opcodes under test ----
            0xD7440003, 0x040A0300, // v_perm_b32 v3, v0, v1, v2
            0x7E087700,             // v_ffbh_i32 v4, v0
            0xD7510008, 0x041E0D05, // v_min3_f16 v8, v5, v6, v7
            0xD752000C, 0x042E1509, // v_min3_i16 v12, v9, v10, v11
            0xD714000D, 0x00021F0E, // v_lshlrev_b16 v13, v14, v15
            0x7E20C511,             // v_sat_pk_u8_i16 v16, v17
            0x7E268112,             // v_frexp_mant_f32 v19, v18
            0x7E2C7F12,             // v_frexp_exp_i32_f32 v22, v18
            0x7E2AAF14,             // v_log_f16 v21, v20
            0xD7110017, 0x00020D05, // v_pack_b32_f16 v23, v5, v6

            // ---- results ----
            0xE0700000, 0x80020300, // buffer_store_dword v3  offset:0
            0xE0700004, 0x80020400, // buffer_store_dword v4  offset:4
            0xE0700008, 0x80020800, // buffer_store_dword v8  offset:8
            0xE070000C, 0x80020C00, // buffer_store_dword v12 offset:12
            0xE0700010, 0x80020D00, // buffer_store_dword v13 offset:16
            0xE0700014, 0x80021000, // buffer_store_dword v16 offset:20
            0xE0700018, 0x80021300, // buffer_store_dword v19 offset:24
            0xE070001C, 0x80021600, // buffer_store_dword v22 offset:28
            0xE0700020, 0x80021500, // buffer_store_dword v21 offset:32
            0xE0700024, 0x80021700, // buffer_store_dword v23 offset:36
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 64);

    [Fact]
    public void NewOpcodesDecodeToTheExpectedInstructions()
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(NewOpcodes);
        var opcodes = program.Instructions.Select(instruction => instruction.Opcode).ToList();
        foreach (var expected in new[]
        {
            "VPermB32", "VFfbhI32", "VMin3F16", "VMin3I16", "VLshlrevB16",
            "VSatPkU8I16", "VFrexpMantF32", "VFrexpExpI32F32", "VLogF16",
            "VPackB32F16",
        })
        {
            Assert.Contains(expected, opcodes);
        }
    }

    [Fact]
    public void NewOpcodesProduceBitExactResultsOnTheGpu()
    {
        if (SkipWithoutMetalDevice("the new-opcode execution test")) return;

        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[64];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var result = ExecuteRequestOrThrow(NewOpcodes, buffer);

        // V_PERM_B32: bytes of {S0, S1} are in[0]=0x44 .. in[3]=0x11, in[4]=0xDD
        // .. in[7]=0xAA. Selector 0x00 -> in[0]=0x44, 0x04 -> in[4]=0xDD,
        // 0x0B -> sign of in[7]=0xAA -> 0xFF, 0x0D (>= 13) -> 0xFF.
        Assert.Equal(0xFFFFDD44u, ReadDword(result, 0));

        // V_FFBH_I32(0xAABBCCDD): negative, so count the bits below bit 31 that
        // match the sign bit. ~0xAABBCCDD = 0x55443322, whose MSB is bit 30.
        Assert.Equal(1u, ReadDword(result, 4));

        // V_MIN3_F16(1.0, 2.0, 0.5) = 0.5h, into the low half; the high half of
        // the destination must survive.
        Assert.Equal(0xBEEF3800u, ReadDword(result, 8));

        // V_MIN3_I16(-3, 5, -1) = -3, with the high half preserved.
        Assert.Equal(0xCAFEFFFDu, ReadDword(result, 12));

        // V_LSHLREV_B16: count from src0 (4), value from src1 (0x1234).
        Assert.Equal(0xAAAA2340u, ReadDword(result, 16));

        // V_SAT_PK_U8_I16({291, -2}) = {sat8(291)=255, sat8(-2)=0}.
        Assert.Equal(0x0000FF00u, ReadDword(result, 20));

        // 12.0f = 0.75 * 2**4, so frexp gives mantissa 0.75 and exponent 4.
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.75f), ReadDword(result, 24));
        Assert.Equal(4u, ReadDword(result, 28));

        // V_LOG_F16(4.0h) = 2.0h = 0x4000, high half preserved.
        Assert.Equal(0xD00D4000u, ReadDword(result, 32));

        // V_PACK_B32_F16(1.0h, 2.0h) = {S1 high, S0 low}.
        Assert.Equal(0x40003C00u, ReadDword(result, 36));

        // Nothing past the last store may be touched.
        for (var offset = 40; offset < result.Length; offset += sizeof(uint))
        {
            Assert.Equal(Sentinel, ReadDword(result, offset));
        }
    }

    private static readonly Gen5ComputeFixture SixteenBitSemantics = new(
        "new-opcodes-16bit",
        [
            0x7E0002FF, 0x00003C00,   // v_mov_b32 v0, 1.0h
            0x7E0202FF, 0x00004000,   // v_mov_b32 v1, 2.0h
            0x7E0402FF, 0x00003800,   // v_mov_b32 v2, 0.5h
            0x7E0602FF, 0x33330000,   // v_mov_b32 v3, med3_f16 destination sentinel
            0xD7570003, 0x040A0300,   // v_med3_f16 v3, v0, v1, v2
            0x7E0802FF, 0x00000007,   // v_mov_b32 v4, 7
            0x7E0A02FF, 0x00000002,   // v_mov_b32 v5, 2
            0x7E0C02FF, 0x00000005,   // v_mov_b32 v6, 5
            0x7E0E02FF, 0x44440000,   // v_mov_b32 v7, med3_u16 destination sentinel
            0xD7590007, 0x041A0B04,   // v_med3_u16 v7, v4, v5, v6
            0x7E1002FF, 0x00000004,   // v_mov_b32 v8, shift count 4
            0x7E1202FF, 0x0000FF00,   // v_mov_b32 v9, 0xFF00 (-256 as i16)
            0x7E1402FF, 0x55550000,   // v_mov_b32 v10, ashrrev destination sentinel
            0xD708000A, 0x00021308,   // v_ashrrev_i16 v10, v8, v9
            0x7E1602FF, 0x00000003,   // v_mov_b32 v11, 3
            0x7E1802FF, 0x66660000,   // v_mov_b32 v12, mad_u16 destination sentinel
            0xD740000C, 0x04120D0B,   // v_mad_u16 v12, v11, v6, v4  (3 * 5 + 7)
            0x7E1A02FF, 0x00000000,   // v_mov_b32 v13, 0.0h
            0x7E1C02FF, 0x77770000,   // v_mov_b32 v14, div_fixup destination sentinel
            0xD75F000E, 0x04021B02,   // v_div_fixup_f16 v14, v2, v13, v0  (x / 0)
            0x7E1E02FF, 0x88880000,   // v_mov_b32 v15, div_fixup destination sentinel
            0xD75F000F, 0x04361B02,   // v_div_fixup_f16 v15, v2, v13, v13 (0 / 0)
            0x7E2002FF, 0x99990000,   // v_mov_b32 v16, cvt_f16_u16 destination sentinel
            0x7E20A104,               // v_cvt_f16_u16 v16, v4
            0x7E2402FF, 0x00000001,   // v_mov_b32 v18, 1
            0x7E2602FF, 0x00000002,   // v_mov_b32 v19, 2
            0xD6FF0014, 0x00022508,   // v_lshlrev_b64 v[20:21], v8, v[18:19]
            0x7E2C02FF, 0x00001234,   // v_mov_b32 v22, 0x1234
            0x7E2E02FF, 0xABCD0000,   // v_mov_b32 v23, mul_lo_u16 destination sentinel
            0xD7050017, 0x00021716,   // v_mul_lo_u16 v23, v22, v11
            0xE0700000, 0x80020300,   // buffer_store_dword v3  offset:0
            0xE0700004, 0x80020700,   // buffer_store_dword v7  offset:4
            0xE0700008, 0x80020A00,   // buffer_store_dword v10 offset:8
            0xE070000C, 0x80020C00,   // buffer_store_dword v12 offset:12
            0xE0700010, 0x80020E00,   // buffer_store_dword v14 offset:16
            0xE0700014, 0x80020F00,   // buffer_store_dword v15 offset:20
            0xE0700018, 0x80021000,   // buffer_store_dword v16 offset:24
            0xE070001C, 0x80021400,   // buffer_store_dword v20 offset:28
            0xE0700020, 0x80021500,   // buffer_store_dword v21 offset:32
            0xE0700024, 0x80021700,   // buffer_store_dword v23 offset:36
            0xBF810000,               // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 64);

    [Fact]
    public void SixteenBitSemanticsAreBitExactOnTheGpu()
    {
        if (SkipWithoutMetalDevice("the 16-bit semantics test")) return;

        var result = ExecuteRequestOrThrow(SixteenBitSemantics, new byte[64]);

        // med3 = max(min(a, b), min(max(a, b), c)); med3(1.0, 2.0, 0.5) = 1.0h.
        Assert.Equal(0x3333_3C00u, ReadDword(result, 0));
        // med3_u16(7, 2, 5) = 5.
        Assert.Equal(0x4444_0005u, ReadDword(result, 4));
        // V_ASHRREV_I16: -256 >> 4 = -16, so the sign must be extended from bit 15.
        Assert.Equal(0x5555_FFF0u, ReadDword(result, 8));
        // V_MAD_U16: 3 * 5 + 7 = 22.
        Assert.Equal(0x6666_0016u, ReadDword(result, 12));
        // V_DIV_FIXUP_F16 with denominator 0 and numerator 1.0 gives +INF, and
        // 0 / 0 gives a quiet NaN, both regardless of the quotient operand.
        Assert.Equal(0x7777_7C00u, ReadDword(result, 16));
        Assert.Equal(0x8888_7E00u, ReadDword(result, 20));
        // V_CVT_F16_U16(7) = 7.0h.
        Assert.Equal(0x9999_4700u, ReadDword(result, 24));
        // V_LSHLREV_B64: 0x0000000200000001 << 4 across the VGPR pair.
        Assert.Equal(0x0000_0010u, ReadDword(result, 28));
        Assert.Equal(0x0000_0020u, ReadDword(result, 32));
        // V_MUL_LO_U16: (0x1234 * 3) & 0xFFFF.
        Assert.Equal(0xABCD_369Cu, ReadDword(result, 36));
    }

    private static readonly Gen5ComputeFixture Float16FmaLiterals = new(
        "f16-fma-literals",
        [
            0x7E0002FF, 0x00004200,   // v_mov_b32 v0, 3.0h
            0x7E0202FF, 0x00003C00,   // v_mov_b32 v1, 1.0h
            0x7E0402FF, 0x11110000,   // v_mov_b32 v2, fmamk destination sentinel
            0x7E0602FF, 0x22220000,   // v_mov_b32 v3, fmaak destination sentinel
            // D = S0 * K + S1 = 3 * 4 + 1 = 13.0h. The literal's high half is
            // garbage on purpose: only its low 16 bits are the f16 value.
            0x6E040300, 0xDEAD4400,   // v_fmamk_f16 v2, v0, 0xDEAD4400, v1
            // D = S0 * S1 + K = 3 * 1 + 4 = 7.0h, same three values in the other
            // roles, so swapping the mk/ak operand order would change the answer.
            0x70060300, 0xBEEF4400,   // v_fmaak_f16 v3, v0, v1, 0xBEEF4400
            0xE0700000, 0x80020200,   // buffer_store_dword v2 offset:0
            0xE0700004, 0x80020300,   // buffer_store_dword v3 offset:4
            0xBF810000,               // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 64);

    [Fact]
    public void Float16FmaLiteralFormsAreBitExactOnTheGpu()
    {
        if (SkipWithoutMetalDevice("the f16 fma literal test")) return;

        var result = ExecuteRequestOrThrow(Float16FmaLiterals, new byte[64]);

        // 3 * 4 + 1 = 13.0h, into the low half, high half preserved.
        Assert.Equal(0x1111_4A80u, ReadDword(result, 0));
        // 3 * 1 + 4 = 7.0h, into the low half, high half preserved.
        Assert.Equal(0x2222_4700u, ReadDword(result, 4));
    }

    private static readonly Gen5ComputeFixture Vop2Accumulators = new(
        "vop2-accumulators",
        [
            0x7E0002FF, 0xFF030201,   // v_mov_b32 v0, signed bytes 1, 2, 3, -1
            0x7E0202FF, 0x07060504,   // v_mov_b32 v1, signed bytes 4, 5, 6, 7
            0x7E0402FF, 0x0000000A,   // v_mov_b32 v2, dot4c accumulator 10
            0x7E0602FF, 0x40004200,   // v_mov_b32 v3, {hi 2.0h, lo 3.0h}
            0x7E0802FF, 0x42004000,   // v_mov_b32 v4, {hi 3.0h, lo 2.0h}
            0x7E0A02FF, 0x3C004400,   // v_mov_b32 v5, {hi 1.0h, lo 4.0h}
            0x7E0C02FF, 0x00000000,   // v_mov_b32 v6, 0.0f
            0x7E0E02FF, 0x7F800000,   // v_mov_b32 v7, +INF
            0x7E1002FF, 0x40000000,   // v_mov_b32 v8, 2.0f
            0x1A040300,               // v_dot4c_i32_i8 v2, v0, v1
            0x780A0903,               // v_pk_fmac_f16 v5, v3, v4
            0x0C100F06,               // v_fmac_legacy_f32 v8, v6, v7
            0xE0700000, 0x80020200,   // buffer_store_dword v2 offset:0
            0xE0700004, 0x80020500,   // buffer_store_dword v5 offset:4
            0xE0700008, 0x80020800,   // buffer_store_dword v8 offset:8
            0xBF810000,               // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 64);

    [Fact]
    public void Vop2AccumulatorFormsAreBitExactOnTheGpu()
    {
        if (SkipWithoutMetalDevice("the VOP2 accumulator test")) return;

        var result = ExecuteRequestOrThrow(Vop2Accumulators, new byte[64]);

        // 1*4 + 2*5 + 3*6 + (-1)*7 + 10 = 35; the bytes are signed.
        Assert.Equal(35u, ReadDword(result, 0));
        // Per half: lo 3*2 + 4 = 10.0h, hi 2*3 + 1 = 7.0h.
        Assert.Equal(0x4700_4900u, ReadDword(result, 4));
        // DX9 rule: 0 * INF is 0, not NaN, so the accumulator survives unchanged.
        Assert.Equal(BitConverter.SingleToUInt32Bits(2.0f), ReadDword(result, 8));
    }

    private static byte[] ExecuteRequestOrThrow(Gen5ComputeFixture fixture, byte[] buffer)
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
        if (!MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError))
        {
            throw new InvalidOperationException(
                $"[{fixture.Name}] {compileError}\n{shader.Source}");
        }

        var pushData = new byte[Resources.PushData.ByteSize];
        if (!MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                buffer,
                pushData,
                shader.ArgumentLayout
                    ?? throw new InvalidOperationException($"[{fixture.Name}] no argument layout"),
                1,
                out var result,
                out var runError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] {runError}");
        }

        return result;
    }

    private bool SkipWithoutMetalDevice(string activity)
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() &&
            Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            $"The required gate cannot run {activity} without a Metal device.");
        output.WriteLine($"No Metal device on this host; {activity} skipped.");
        return true;
    }

    private static uint ReadDword(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
