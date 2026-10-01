// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    private static readonly bool RepeatingCopyEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_REPEATING_COPY_FAST_PATH") == "1";
    private int _repeatingCopyCompleted;

    // Exact ISA shape for dst[id] = src[id % period], id < count. The compiler
    // implements unsigned division with a reciprocal and integer corrections.
    // No shader hash, guest address, title or source contents participate in
    // recognition. Only the unused trace-marker immediate (word 1) may vary.
    internal static ReadOnlySpan<uint> RepeatingCopyWords =>
    [
        0xBEFC03FFu, 0xA65CD91Eu, 0xBF960000u, 0xBFA00003u, 0xD7460002u, 0x04010C0Cu,
        0xF4201A84u, 0xFA000000u, 0xBF8CC07Fu, 0x7DA8046Au, 0xBF88002Au, 0xF4200304u,
        0xFA000004u, 0xBF8CC07Fu, 0x7E000C0Cu, 0xBF070C80u, 0x8588807Eu, 0x7E005700u,
        0x100000FFu, 0x4F800000u, 0x7E060F00u, 0xD5766A00u, 0x0202060Cu, 0x7D8A0280u,
        0x4C020080u, 0x02000101u, 0xD56A0001u, 0x00020700u, 0x4C000303u, 0x4A020303u,
        0x02000101u, 0xD56A0000u, 0x00020500u, 0xD5690001u, 0x0002000Cu, 0x4C060302u,
        0x7D8C02F9u, 0x06068A02u, 0x7D86060Cu, 0x87EA6A0Au, 0x50000080u, 0xD5286A00u,
        0x002A00C1u, 0xD5010000u, 0x002200C1u, 0xD5690000u, 0x0002000Cu, 0x4C000102u,
        0xE0002000u, 0x80000000u, 0xBF8C3F70u, 0xE0102000u, 0x80010002u, 0xBF810000u,
    ];

    internal static bool IsRepeatingDwordCopyKernel(Gen5ShaderProgram program)
    {
        var expected = RepeatingCopyWords;
        var index = 0;
        foreach (var instruction in program.Instructions)
            foreach (var word in instruction.Words)
            {
                if (index >= expected.Length || (index != 1 && word != expected[index])) return false;
                index++;
            }
        return index == expected.Length;
    }

    private bool TrySubmitRepeatingDwordCopyKernel(Gen5ShaderProgram program, ShaderSource source,
        Gen5ComputeSystemRegisters systemRegisters, ComputeInputInfo input)
    {
        if (!RepeatingCopyEnabled || !IsRepeatingDwordCopyKernel(program) ||
            input.ThreadsX != 64 || input.ThreadsY != 1 || input.ThreadsZ != 1 ||
            systemRegisters.WorkGroupXRegister != WorkGroupRegisterOfCopyKernel)
            return false;
        var groupsX = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsX, input.ThreadsX) : input.DispatchThreadsX;
        var groupsY = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsY, input.ThreadsY) : input.DispatchThreadsY;
        var groupsZ = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsZ, input.ThreadsZ) : input.DispatchThreadsZ;
        if (groupsY != 1 || groupsZ != 1 || source.UserData.Length < 12 ||
            !IsExactMaskedDwordCopyDescriptor(source.UserData, 0, out var from) ||
            !IsExactMaskedDwordCopyDescriptor(source.UserData, 4, out var to))
            return false;
        var control = BufferDescriptorWords.From(source.UserData.AsSpan(8, 4));
        var controlSize = control.Footprint() ?? 0;
        if (control.Address == 0 || controlSize < 8 || from.Address == 0 || to.Address == 0 ||
            (control.Word1 & (1u << 30)) != 0 || control.SwizzleEnabled || control.AddThreadId || control.Type != 0 || control.OutOfBounds != 0)
            return false;
        Span<byte> parameters = stackalloc byte[8];
        // Do not read stale backing bytes. A GPU-owned input declines this
        // optimization and executes the original shader in command order.
        if (!_host.TryReadResidentGuestBytes(control.Address, parameters, clean: true)) return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        var period = BinaryPrimitives.ReadUInt32LittleEndian(parameters[4..]);
        var dispatchedThreads = input.DispatchThreadDimensions ? input.DispatchThreadsX : (ulong)groupsX * input.ThreadsX;
        var outputCount = (uint)Math.Min(Math.Min((ulong)count, dispatchedThreads), (to.Footprint() ?? 0) / 4);
        if (period == 0 || outputCount == 0 || outputCount > MaxCopyKernelSourceBytes / 4) return false;
        var sourceBytes = Math.Min(from.Footprint() ?? 0, Math.Min((ulong)period, outputCount) * 4);
        if (sourceBytes == 0 || sourceBytes > MaxCopyKernelSourceBytes) return false;
        if (!TryCopyRepeatingDwords(_context.Memory, from.Address, (int)sourceBytes, to.Address, outputCount, period,
                _host.TryReadResidentGuestBytes)) return false;
        if (++_repeatingCopyCompleted <= 8)
            Console.Error.WriteLine($"[PERF][REPEATING_COPY] completed={_repeatingCopyCompleted} source=0x{from.Address:X} destination=0x{to.Address:X} count={outputCount} period={period}");
        return true;
    }

    internal static bool TryCopyRepeatingDwords(ICpuMemory memory, ulong sourceAddress, int sourceBytes,
        ulong destinationAddress, uint outputDwords, uint period, ResidentGuestBytesReader? residentReader = null)
    {
        if (period == 0 || sourceBytes < 4 || sourceBytes % 4 != 0 || outputDwords == 0 || outputDwords > int.MaxValue / 4)
            return false;
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(sourceBytes);
        byte[]? outputBuffer = null;
        try
        {
            var source = sourceBuffer.AsSpan(0, sourceBytes);
            if (!(residentReader is null ? memory.TryRead(sourceAddress, source) : residentReader(sourceAddress, source, clean: true)))
                return false;
            var outputBytes = (int)outputDwords * 4;
            outputBuffer = ArrayPool<byte>.Shared.Rent(outputBytes);
            var output = outputBuffer.AsSpan(0, outputBytes);
            var inputWords = MemoryMarshal.Cast<byte, uint>(source);
            var outputWords = MemoryMarshal.Cast<byte, uint>(output);
            for (uint index = 0; index < outputDwords; index++)
            {
                var sourceIndex = index % period;
                outputWords[(int)index] = sourceIndex < (uint)inputWords.Length ? inputWords[(int)sourceIndex] : 0;
            }
            // TryWrite performs the normal guest GPU/image invalidation. The
            // complete source snapshot also preserves overlapping copy inputs.
            return memory.TryWrite(destinationAddress, output);
        }
        finally
        {
            if (outputBuffer is not null) ArrayPool<byte>.Shared.Return(outputBuffer);
            ArrayPool<byte>.Shared.Return(sourceBuffer);
        }
    }
}
