// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// Off Windows a blocked guest thread resumes through one shared trampoline that reads the state
	// it restores from a block the caller passes in RDI, instead of a stub emitted per resume with the
	// state as immediates. Windows keeps the per-resume stub: its stack bounds are baked into it.
	// SHARPEMU_SHARED_CONTINUATION_STUB=0 restores the per-resume stub everywhere.
	private static readonly bool UseSharedContinuationStub = !OperatingSystem.IsWindows() &&
		Environment.GetEnvironmentVariable("SHARPEMU_SHARED_CONTINUATION_STUB") != "0";

	private const int ContinuationBlockQwords = 20;

	// Block layout, in qwords.
	private const int BlockHostRspSlot = 0;
	private const int BlockRsp = 1;
	private const int BlockEntry = 2;
	private const int BlockRbx = 3;
	private const int BlockRbp = 4;
	private const int BlockRdi = 5;
	private const int BlockRsi = 6;
	private const int BlockRdx = 7;
	private const int BlockRcx = 8;
	private const int BlockR8 = 9;
	private const int BlockR9 = 10;
	private const int BlockR10 = 11;
	private const int BlockR12 = 12;
	private const int BlockR13 = 13;
	private const int BlockR14 = 14;
	private const int BlockR15 = 15;
	private const int BlockR11 = 16;
	private const int BlockRax = 17;
	private const int BlockMxcsr = 18;
	private const int BlockFpuControl = 19;

	private static nint _sharedContinuationStub;
	private static readonly object _sharedContinuationStubGate = new();

	private static unsafe void FillContinuationBlock(ulong* block, CpuContext context, ulong hostRspSlot, ulong entryPoint)
	{
		block[BlockHostRspSlot] = hostRspSlot;
		block[BlockRsp] = context[CpuRegister.Rsp];
		block[BlockEntry] = entryPoint;
		block[BlockRbx] = context[CpuRegister.Rbx];
		block[BlockRbp] = context[CpuRegister.Rbp];
		block[BlockRdi] = context[CpuRegister.Rdi];
		block[BlockRsi] = context[CpuRegister.Rsi];
		block[BlockRdx] = context[CpuRegister.Rdx];
		block[BlockRcx] = context[CpuRegister.Rcx];
		block[BlockR8] = context[CpuRegister.R8];
		block[BlockR9] = context[CpuRegister.R9];
		block[BlockR10] = context[CpuRegister.R10];
		block[BlockR12] = context[CpuRegister.R12];
		block[BlockR13] = context[CpuRegister.R13];
		block[BlockR14] = context[CpuRegister.R14];
		block[BlockR15] = context[CpuRegister.R15];
		block[BlockR11] = context[CpuRegister.R11];
		block[BlockRax] = context[CpuRegister.Rax];
		block[BlockMxcsr] = context.Mxcsr;
		block[BlockFpuControl] = context.FpuControlWord;
	}

	private static unsafe void* GetSharedContinuationStub()
	{
		var stub = System.Threading.Volatile.Read(ref _sharedContinuationStub);
		if (stub != 0)
		{
			return (void*)stub;
		}

		lock (_sharedContinuationStubGate)
		{
			if (_sharedContinuationStub == 0)
			{
				_sharedContinuationStub = (nint)CreateSharedContinuationStub();
			}

			return (void*)_sharedContinuationStub;
		}
	}

	// The host side matches the per-resume stub exactly (the guest return stub unwinds it): the host
	// nonvolatile registers and XMM6-15 are saved, then RSP is published through the host-RSP slot.
	private static unsafe void* CreateSharedContinuationStub()
	{
		const uint stubSize = 512u;
		void* memory = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (memory == null)
		{
			throw new OutOfMemoryException("Failed to allocate the shared guest continuation stub");
		}

		var code = (byte*)memory;
		var emitter = new NativeCodeEmitter(code);
		emitter.Emit(0x53); // push rbx
		emitter.Emit(0x55); // push rbp
		emitter.Emit(0x57); // push rdi
		emitter.Emit(0x56); // push rsi
		emitter.Emit(0x41); emitter.Emit(0x54); // push r12
		emitter.Emit(0x41); emitter.Emit(0x55); // push r13
		emitter.Emit(0x41); emitter.Emit(0x56); // push r14
		emitter.Emit(0x41); emitter.Emit(0x57); // push r15
		EmitHostNonvolatileXmmSave(code, ref emitter.Offset);
		emitter.Emit(0x49); emitter.Emit(0x89); emitter.Emit(0xFB); // mov r11, rdi (the block)
		EmitBlockMemoryOperand(ref emitter, [0x41, 0x0F, 0xAE], 2, BlockMxcsr); // ldmxcsr [r11+mxcsr]
		EmitBlockMemoryOperand(ref emitter, [0x41, 0xD9], 5, BlockFpuControl); // fldcw [r11+fpu]
		EmitLoadFromBlock(ref emitter, 10, BlockHostRspSlot); // mov r10, [r11+slot]
		emitter.Emit(0x49); emitter.Emit(0x89); emitter.Emit(0x22); // mov [r10], rsp
		EmitLoadFromBlock(ref emitter, 4, BlockRsp); // mov rsp, [r11+rsp]
		emitter.Emit(0x48); emitter.Emit(0x83); emitter.Emit(0xEC); emitter.Emit(0x08); // reserve transfer slot
		EmitLoadFromBlock(ref emitter, 0, BlockEntry); // mov rax, [r11+entry]
		emitter.Emit(0x48); emitter.Emit(0x89); emitter.Emit(0x04); emitter.Emit(0x24); // mov [rsp], rax
		EmitLoadFromBlock(ref emitter, 3, BlockRbx);
		EmitLoadFromBlock(ref emitter, 5, BlockRbp);
		EmitLoadFromBlock(ref emitter, 7, BlockRdi);
		EmitLoadFromBlock(ref emitter, 6, BlockRsi);
		EmitLoadFromBlock(ref emitter, 2, BlockRdx);
		EmitLoadFromBlock(ref emitter, 1, BlockRcx);
		EmitLoadFromBlock(ref emitter, 8, BlockR8);
		EmitLoadFromBlock(ref emitter, 9, BlockR9);
		EmitLoadFromBlock(ref emitter, 10, BlockR10);
		EmitLoadFromBlock(ref emitter, 12, BlockR12);
		EmitLoadFromBlock(ref emitter, 13, BlockR13);
		EmitLoadFromBlock(ref emitter, 14, BlockR14);
		EmitLoadFromBlock(ref emitter, 15, BlockR15);
		EmitLoadFromBlock(ref emitter, 0, BlockRax);
		EmitLoadFromBlock(ref emitter, 11, BlockR11); // the base register goes last
		emitter.Emit(0xC3); // ret through the synthetic transfer slot

		uint oldProtect = 0;
		if (!VirtualProtect(memory, stubSize, 32u, &oldProtect))
		{
			throw new InvalidOperationException("Failed to seal the shared guest continuation stub execute-read");
		}

		FlushInstructionCache(GetCurrentProcess(), memory, stubSize);
		return memory;
	}

	// mov reg64, [r11 + qword*8]
	private static void EmitLoadFromBlock(ref NativeCodeEmitter emitter, int register, int qword)
	{
		emitter.Emit((byte)(0x49 | (register >= 8 ? 0x04 : 0x00))); // REX.W + REX.B (+ REX.R)
		emitter.Emit(0x8B);
		EmitR11Displacement(ref emitter, register & 7, qword * sizeof(ulong));
	}

	// An instruction whose memory operand is [r11 + qword*8], with the given ModRM reg field.
	private static void EmitBlockMemoryOperand(ref NativeCodeEmitter emitter, ReadOnlySpan<byte> prefixAndOpcode, int regField, int qword)
	{
		foreach (var value in prefixAndOpcode)
		{
			emitter.Emit(value);
		}

		EmitR11Displacement(ref emitter, regField, qword * sizeof(ulong));
	}

	private static void EmitR11Displacement(ref NativeCodeEmitter emitter, int regField, int displacement)
	{
		if (displacement <= sbyte.MaxValue)
		{
			emitter.Emit((byte)(0x40 | (regField << 3) | 3)); // mod=01, rm=r11
			emitter.Emit((byte)displacement);
		}
		else
		{
			emitter.Emit((byte)(0x80 | (regField << 3) | 3)); // mod=10, rm=r11
			emitter.Emit((uint)displacement);
		}
	}
}
