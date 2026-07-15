// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	// POSIX bridge for the Windows vectored-exception-handler logic. A
	// sigaction(SIGSEGV/SIGBUS/SIGILL) handler rebuilds the EXCEPTION_POINTERS
	// view the shared handlers expect (Win64 CONTEXT register offsets) from
	// the signal's mcontext, runs the same recovery chain the VEH path uses
	// (unresolved-import trap sentinels, demand-paging of lazily-committed
	// guest pages, fault diagnostics), and writes register changes back into
	// the mcontext so sigreturn resumes the repaired guest. Unrecovered
	// faults are forwarded to the previously installed handler so the .NET
	// runtime keeps turning its own faults into managed exceptions.

	private const int PosixSigIll = 4;
	private const int PosixSigSegv = 11;
	private static readonly int PosixSigBus = OperatingSystem.IsMacOS() ? 10 : 7;
	private static readonly int PosixSigUsr2 = OperatingSystem.IsMacOS() ? 31 : 12;

	// struct sigaction: the handler pointer leads on both platforms; Darwin
	// packs { handler(8), mask(4), flags(4) }, Linux glibc/musl packs
	// { handler(8), mask(128), flags(4), restorer(8) }.
	private static readonly int PosixSigactionSize = OperatingSystem.IsMacOS() ? 16 : 152;
	private static readonly int PosixSigactionFlagsOffset = OperatingSystem.IsMacOS() ? 12 : 136;

	private static readonly int PosixSaSigInfo = OperatingSystem.IsMacOS() ? 0x0040 : 0x0004;
	private static readonly int PosixSaNoDefer = OperatingSystem.IsMacOS() ? 0x0010 : 0x40000000;

	// siginfo_t.si_addr: Darwin { signo, errno, code, pid, uid, status, addr },
	// Linux { signo, errno, code, pad32, addr }.
	private static readonly int PosixSigInfoAddressOffset = OperatingSystem.IsMacOS() ? 24 : 16;

	// Darwin ucontext_t stores a pointer to __darwin_mcontext64 at +48; the
	// general registers live in its __ss thread state after the 16-byte
	// exception state. Linux glibc embeds mcontext_t inline at +40 with the
	// registers in gregs[23]. Rosetta 2 delivers the regular x86-64 layout
	// to translated processes.
	private const int DarwinUcontextMcontextOffset = 48;
	private const int DarwinMcontextErrOffset = 4;
	private const int DarwinMcontextFaultAddressOffset = 8;
	private const int LinuxUcontextGregsOffset = 40;
	private const int LinuxGregsErrOffset = 19 * 8;

	// Byte offsets of the general registers relative to GetPosixRegisterBase,
	// ordered to match the contiguous Win64 CONTEXT block CTX_RAX..CTX_RIP
	// (rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8..r15, rip). Verified
	// against the x86-64 platform headers.
	private static readonly int[] PosixRegisterOffsets = OperatingSystem.IsMacOS()
		? new[] { 16, 32, 40, 24, 72, 64, 56, 48, 80, 88, 96, 104, 112, 120, 128, 136, 144 }
		: new[] { 104, 112, 96, 88, 120, 80, 72, 64, 0, 8, 16, 24, 32, 40, 48, 56, 128 };

	private static DirectExecutionBackend? _posixSignalBackend;
	private static bool _posixSignalHandlersInstalled;
	private static bool _posixRawRecoveryEnabled;
	private static bool _posixSignalWarmup;
	private static readonly nint[] _posixPreviousActions = new nint[32];
	private static int _posixSignalTraceCount;
	private static long _perfSignalCount;
	private static nint _posixSamplePreviousAction;
	private static nint _posixSampleTargetThread;
	private static uint _posixSampleTargetMachThread;
	private static Thread? _posixSamplerThread;
	private static volatile bool _posixSamplerStop;
	private static int _posixSampleSequence;
	private static int _posixSampleDisasmDumped;
	private static PosixRegisterSample _posixRegisterSample;
	private static readonly bool _perfSignalCounter =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PERF_MEM"), "1", StringComparison.Ordinal);

	private struct PosixRegisterSample
	{
		public ulong Rax;
		public ulong Rcx;
		public ulong Rdx;
		public ulong Rbx;
		public ulong Rsp;
		public ulong Rbp;
		public ulong Rsi;
		public ulong Rdi;
		public ulong R8;
		public ulong R9;
		public ulong R10;
		public ulong R11;
		public ulong R12;
		public ulong R13;
		public ulong R14;
		public ulong R15;
		public ulong Rip;
	}

	[ThreadStatic]
	private static int _posixSignalHandlerDepth;

	private void SetupPosixExceptionHandler()
	{
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_POSIX_SIGNALS"), "1", StringComparison.Ordinal))
		{
			Console.Error.WriteLine("[LOADER][WARN] POSIX signal exception bridge disabled by SHARPEMU_DISABLE_POSIX_SIGNALS=1; guest faults will not be recovered.");
			return;
		}

		_posixSignalBackend = this;
		if (_posixSignalHandlersInstalled)
		{
			return;
		}

		_posixRawRecoveryEnabled = !string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal);
		if (!_posixRawRecoveryEnabled)
		{
			Console.Error.WriteLine("[LOADER][INFO] Raw sentinel recovery disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
		}

		WarmUpPosixSignalPath();
		SharpEmu.HLE.GuestImageWriteTracker.WarmUp();

		if (!InstallPosixSignalHandler(PosixSigSegv) ||
			!InstallPosixSignalHandler(PosixSigBus) ||
			!InstallPosixSignalHandler(PosixSigIll))
		{
			throw new InvalidOperationException("Failed to install POSIX fault signal handlers");
		}

		_posixSignalHandlersInstalled = true;
		Console.Error.WriteLine("[LOADER][INFO] POSIX signal exception bridge installed (SIGSEGV/SIGBUS/SIGILL)");
	}

	/// <summary>
	/// Runs the signal-recovery path once with fabricated inputs before the
	/// handlers are installed. The first entry into the handler must not
	/// require JIT compilation (a fault can interrupt arbitrary runtime
	/// states), and under Rosetta 2 the signal trampoline cannot enter x86
	/// code that has never been executed (and therefore never translated): a
	/// cold handler is silently never invoked and the faulting instruction
	/// retries forever.
	/// </summary>
	private void WarmUpPosixSignalPath()
	{
		byte* fakeUcontext = stackalloc byte[512];
		new Span<byte>(fakeUcontext, 512).Clear();
		byte* fakeMcontext = stackalloc byte[512];
		new Span<byte>(fakeMcontext, 512).Clear();
		if (OperatingSystem.IsMacOS())
		{
			*(byte**)(fakeUcontext + DarwinUcontextMcontextOffset) = fakeMcontext;
		}

		_posixSignalWarmup = true;
		try
		{
			((delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal)(PosixSigSegv, 0, (nint)fakeUcontext);

			// Warm the branches the fabricated fault above skips without
			// spamming diagnostics: the benign-exception path through
			// VectoredHandler, the lazy-commit probe (fault address 0 bails
			// out immediately), and the chain helper (signal 0 has no saved
			// action and sigaction(0, ...) fails with EINVAL).
			EXCEPTION_RECORD record = default;
			record.ExceptionCode = DBG_PRINTEXCEPTION_C;
			byte* contextRecord = stackalloc byte[Win64ContextSize];
			new Span<byte>(contextRecord, Win64ContextSize).Clear();
			EXCEPTION_POINTERS pointers;
			pointers.ExceptionRecord = &record;
			pointers.ContextRecord = contextRecord;
			_ = VectoredHandler(&pointers);

			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			// 0x70000 is never guest-owned, so this walks the vmem region
			// scan and the PRT range check, then bails out silently.
			record.ExceptionInformation[1] = 0x70000;
			_ = TryHandleLazyCommittedPage(&record, 0, 0);
			ChainPreviousPosixAction(0, 0, 0);
		}
		finally
		{
			_posixSignalWarmup = false;
		}
	}

	private static bool InstallPosixSignalHandler(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		*(nint*)action = (nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal;
		// No SA_ONSTACK: the runtime's alternate stacks are far too small for
		// the recovery/diagnostic path (JIT compilation of cold handler code
		// can run inside the signal frame). Guest faults deliver onto the 2MB
		// guest stack, host faults onto the regular thread stack — the same
		// stacks Windows dispatches exceptions on.
		*(int*)(action + PosixSigactionFlagsOffset) = PosixSaSigInfo | PosixSaNoDefer;

		var previous = (byte*)NativeMemory.AllocZeroed((nuint)PosixSigactionSize);
		if (sigaction(signal, action, previous) != 0)
		{
			NativeMemory.Free(previous);
			Console.Error.WriteLine($"[LOADER][ERROR] sigaction({signal}) failed: errno={Marshal.GetLastPInvokeError()}");
			return false;
		}

		_posixPreviousActions[signal] = (nint)previous;
		return true;
	}

	private void StartPosixRegisterSampler()
	{
		if (OperatingSystem.IsWindows() ||
			!int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_POSIX_SAMPLE_MS"), out int sampleMilliseconds) ||
			sampleMilliseconds <= 0 ||
			_posixSamplerThread != null)
		{
			return;
		}

		if (!OperatingSystem.IsMacOS())
		{
			// Warm the diagnostic callback before a signal frame enters it.
			byte* fakeUcontext = stackalloc byte[512];
			new Span<byte>(fakeUcontext, 512).Clear();
			((delegate* unmanaged<int, nint, nint, void>)&HandlePosixSampleSignal)(
				PosixSigUsr2,
				0,
				(nint)fakeUcontext);

			byte* action = stackalloc byte[PosixSigactionSize];
			new Span<byte>(action, PosixSigactionSize).Clear();
			*(nint*)action = (nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSampleSignal;
			*(int*)(action + PosixSigactionFlagsOffset) = PosixSaSigInfo | PosixSaNoDefer;

			byte* previous = (byte*)NativeMemory.AllocZeroed((nuint)PosixSigactionSize);
			if (sigaction(PosixSigUsr2, action, previous) != 0)
			{
				NativeMemory.Free(previous);
				Console.Error.WriteLine($"[CPU][WARN] POSIX register sampler sigaction failed: errno={Marshal.GetLastPInvokeError()}");
				return;
			}
			_posixSamplePreviousAction = (nint)previous;
		}

		_posixSampleTargetThread = pthread_self();
		_posixSampleTargetMachThread = OperatingSystem.IsMacOS()
			? pthread_mach_thread_np(_posixSampleTargetThread)
			: 0;
		_posixSamplerStop = false;
		Volatile.Write(ref _posixSampleDisasmDumped, 0);
		_posixSamplerThread = new Thread(() => RunPosixRegisterSampler(sampleMilliseconds))
		{
			IsBackground = true,
			Name = "SharpEmu.PosixRegisterSampler"
		};
		_posixSamplerThread.Start();
		Console.Error.WriteLine($"[CPU][INFO] POSIX guest register sampler enabled ({sampleMilliseconds} ms)");
	}

	private void RunPosixRegisterSampler(int sampleMilliseconds)
	{
		int observedSequence = Volatile.Read(ref _posixSampleSequence);
		while (!_posixSamplerStop)
		{
			Thread.Sleep(sampleMilliseconds);
			if (_posixSamplerStop)
			{
				break;
			}

			PosixRegisterSample sample;
			if (OperatingSystem.IsMacOS())
			{
				if (!TryCaptureMacRegisterSample(out sample))
				{
					continue;
				}
			}
			else
			{
				nint target = _posixSampleTargetThread;
				if (target == 0 || pthread_kill(target, PosixSigUsr2) != 0)
				{
					continue;
				}

				int sequence = Volatile.Read(ref _posixSampleSequence);
				if (sequence == observedSequence)
				{
					Thread.Yield();
					sequence = Volatile.Read(ref _posixSampleSequence);
				}
				if (sequence == observedSequence)
				{
					continue;
				}
				observedSequence = sequence;
				sample = _posixRegisterSample;
			}
			if (sample.Rip < GuestImageScanStart || sample.Rip >= GuestImageScanEnd)
			{
				continue;
			}

			if (Interlocked.CompareExchange(ref _posixSampleDisasmDumped, 1, 0) == 0)
			{
				foreach (ulong address in ParseDiagnosticAddresses(
					Environment.GetEnvironmentVariable("SHARPEMU_POSIX_SAMPLE_DISASM_ADDRS")))
				{
					DumpGuestInstructionStream($"posix-sample-0x{address:X16}", address, 192);
				}
			}

			string r12Value = TryReadSamplerUInt64(sample.R12, out ulong r12) ? $"0x{r12:X16}" : "unreadable";
			string r13Value = TryReadSamplerUInt64(sample.R13, out ulong r13) ? $"0x{r13:X16}" : "unreadable";
			string r15Value = TryReadSamplerUInt64(sample.R15, out ulong r15) ? $"0x{r15:X16}" : "unreadable";
			string frameCount = sample.Rbp >= 0x1B8 && TryReadSamplerUInt64(sample.Rbp - 0x1B8, out ulong count) ? $"0x{count:X16}" : "unreadable";
			string frameIndex = sample.Rbp >= 0x1F0 && TryReadSamplerUInt64(sample.Rbp - 0x1F0, out ulong index) ? $"0x{index:X16}" : "unreadable";
			string archiveState = TryReadSamplerUInt64(sample.R13 + 8, out ulong state) ? $"0x{state:X16}" : "unreadable";
			ulong cursor = 0;
			string archiveCursor = state != 0 && TryReadSamplerUInt64(state, out cursor) ? $"0x{cursor:X16}" : "unreadable";
			string archiveEnd = state != 0 && TryReadSamplerUInt64(state + 8, out ulong end) ? $"0x{end:X16}" : "unreadable";
			string cursorData = cursor != 0 && TryReadSamplerUInt64(cursor, out ulong data) ? $"0x{data:X16}" : "unreadable";
			string archiveFlags = TryReadSamplerUInt64(sample.R13 + 0x28, out ulong flags) ? $"0x{flags:X16}" : "unreadable";
			string archiveParent = TryReadSamplerUInt64(sample.R13 + 0x90, out ulong parent) ? $"0x{parent:X16}" : "unreadable";
			string archiveSize = TryReadSamplerUInt64(sample.R13 + 0xA8, out ulong size) ? $"0x{size:X16}" : "unreadable";
			string archivePosition = TryReadSamplerUInt64(sample.R13 + 0xB0, out ulong position) ? $"0x{position:X16}" : "unreadable";
			string archiveB8 = TryReadSamplerUInt64(sample.R13 + 0xB8, out ulong b8) ? $"0x{b8:X16}" : "unreadable";
			string archiveC8 = TryReadSamplerUInt64(sample.R13 + 0xC8, out ulong c8) ? $"0x{c8:X16}" : "unreadable";
			string archiveD0 = TryReadSamplerUInt64(sample.R13 + 0xD0, out ulong d0) ? $"0x{d0:X16}" : "unreadable";
			string archiveD8 = TryReadSamplerUInt64(sample.R13 + 0xD8, out ulong d8) ? $"0x{d8:X16}" : "unreadable";
			string archiveE0 = TryReadSamplerUInt64(sample.R13 + 0xE0, out ulong e0) ? $"0x{e0:X16}" : "unreadable";
			string serializeVfunc = r13 != 0 && TryReadSamplerUInt64(r13 + 0x158, out ulong vfunc) ? $"0x{vfunc:X16}" : "unreadable";
			string archiveSelector = TryReadSamplerUInt64(sample.Rbp - 0x20, out ulong selectorAddress) &&
				TryReadSamplerUtf16String(selectorAddress, out string selector)
					? $"0x{selectorAddress:X16}:'{selector}'"
					: "unreadable";
			string deserializerName = TryReadSamplerUInt64(sample.Rbp - 0x200, out ulong nameAddress) &&
				TryReadSamplerUtf16String(nameAddress, out string name)
					? $"0x{nameAddress:X16}:'{name}'"
					: "unreadable";
			string frameBacktrace = BuildSamplerFrameBacktrace(sample.Rbp);
			Console.Error.WriteLine(
				$"[CPU][SAMPLE] rip=0x{sample.Rip:X16} rsp=0x{sample.Rsp:X16} rbp=0x{sample.Rbp:X16} " +
				$"rax=0x{sample.Rax:X16} rbx=0x{sample.Rbx:X16} rcx=0x{sample.Rcx:X16} rdx=0x{sample.Rdx:X16} " +
				$"rsi=0x{sample.Rsi:X16} rdi=0x{sample.Rdi:X16} r8=0x{sample.R8:X16} r9=0x{sample.R9:X16} " +
				$"r10=0x{sample.R10:X16} r11=0x{sample.R11:X16} " +
				$"r12=0x{sample.R12:X16}->{r12Value} r13=0x{sample.R13:X16}->{r13Value} " +
				$"r14=0x{sample.R14:X16} r15=0x{sample.R15:X16}->{r15Value} " +
				$"frame_count={frameCount} frame_index={frameIndex} archive_state={archiveState} " +
				$"cursor={archiveCursor} end={archiveEnd} cursor_data={cursorData} flags={archiveFlags} " +
				$"parent={archiveParent} size={archiveSize} position={archivePosition} b8={archiveB8} " +
				$"c8={archiveC8} d0={archiveD0} d8={archiveD8} e0={archiveE0} serialize={serializeVfunc} " +
				$"selector={archiveSelector} deserializer_name={deserializerName}");
			Console.Error.WriteLine($"[CPU][SAMPLE] frames={frameBacktrace}");
		}
	}

	private string BuildSamplerFrameBacktrace(ulong framePointer)
	{
		var frames = new StringBuilder(384);
		for (int depth = 0; depth < 24; depth++)
		{
			if (!TryReadSamplerUInt64(framePointer, out ulong nextFramePointer) ||
				!TryReadSamplerUInt64(framePointer + sizeof(ulong), out ulong returnAddress))
			{
				break;
			}

			if (frames.Length != 0)
			{
				frames.Append(" <- ");
			}
			frames.Append("0x");
			frames.Append(returnAddress.ToString("X16"));

			// x86-64 frame chains grow toward the top of the stack. Stop on
			// malformed, cyclic, or implausibly large links rather than walking
			// arbitrary guest memory when a leaf omitted its frame pointer.
			if (nextFramePointer <= framePointer || nextFramePointer - framePointer > 0x100000)
			{
				break;
			}
			framePointer = nextFramePointer;
		}

		return frames.Length != 0 ? frames.ToString() : "unavailable";
	}

	private bool TryReadSamplerUtf16String(ulong stringAddress, out string value)
	{
		value = string.Empty;
		var context = ActiveCpuContext;
		if (context == null || stringAddress == 0 ||
			!context.TryReadUInt64(stringAddress, out ulong dataAddress) ||
			!context.TryReadUInt64(stringAddress + sizeof(ulong), out ulong counts))
		{
			return false;
		}

		int count = unchecked((int)(uint)counts);
		int capacity = unchecked((int)(uint)(counts >> 32));
		if (dataAddress == 0 || count <= 0 || count > 512 || capacity < count || capacity > 1 << 20)
		{
			return false;
		}

		byte[] bytes = new byte[checked(count * sizeof(ushort))];
		if (!context.Memory.TryRead(dataAddress, bytes))
		{
			return false;
		}

		int charCount = count;
		if (charCount > 0 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((charCount - 1) * 2, 2)) == 0)
		{
			charCount--;
		}
		value = Encoding.Unicode.GetString(bytes, 0, charCount * sizeof(ushort));
		return true;
	}

	private bool TryReadSamplerUInt64(ulong address, out ulong value)
	{
		value = 0;
		var context = ActiveCpuContext;
		return context != null && address != 0 && context.TryReadUInt64(address, out value);
	}

	private void StopPosixRegisterSampler()
	{
		Thread? samplerThread = _posixSamplerThread;
		if (samplerThread == null)
		{
			return;
		}

		_posixSamplerStop = true;
		if (samplerThread != Thread.CurrentThread)
		{
			_ = samplerThread.Join(1000);
		}
		_posixSamplerThread = null;
		_posixSampleTargetThread = 0;
		Volatile.Write(ref _posixSampleTargetMachThread, 0);

		nint previous = _posixSamplePreviousAction;
		_posixSamplePreviousAction = 0;
		if (previous != 0)
		{
			_ = sigaction(PosixSigUsr2, (void*)previous, null);
			NativeMemory.Free((void*)previous);
		}
	}

	private void SetPosixRegisterSamplerTargetForCurrentThread()
	{
		if (_posixSamplerThread != null)
		{
			_posixSampleTargetThread = pthread_self();
			if (OperatingSystem.IsMacOS())
			{
				Volatile.Write(
					ref _posixSampleTargetMachThread,
					pthread_mach_thread_np(_posixSampleTargetThread));
			}
		}
	}

	private static bool TryCaptureMacRegisterSample(out PosixRegisterSample sample)
	{
		sample = default;
		uint target = Volatile.Read(ref _posixSampleTargetMachThread);
		if (target == 0 || thread_suspend(target) != 0)
		{
			return false;
		}

		try
		{
			// x86_thread_state64_t is 21 qwords / 42 natural_t values.
			ulong* state = stackalloc ulong[21];
			uint count = 42;
			if (thread_get_state(target, 4, state, ref count) != 0 || count < 42)
			{
				return false;
			}

			sample.Rax = state[0];
			sample.Rbx = state[1];
			sample.Rcx = state[2];
			sample.Rdx = state[3];
			sample.Rdi = state[4];
			sample.Rsi = state[5];
			sample.Rbp = state[6];
			sample.Rsp = state[7];
			sample.R8 = state[8];
			sample.R9 = state[9];
			sample.R10 = state[10];
			sample.R11 = state[11];
			sample.R12 = state[12];
			sample.R13 = state[13];
			sample.R14 = state[14];
			sample.R15 = state[15];
			sample.Rip = state[16];
			return true;
		}
		finally
		{
			_ = thread_resume(target);
		}
	}

	[UnmanagedCallersOnly]
	private static void HandlePosixSampleSignal(int signal, nint siginfo, nint ucontext)
	{
		byte* registers = GetPosixRegisterBase(ucontext);
		if (registers == null)
		{
			return;
		}

		int[] offsets = PosixRegisterOffsets;
		_posixRegisterSample.Rax = *(ulong*)(registers + offsets[0]);
		_posixRegisterSample.Rcx = *(ulong*)(registers + offsets[1]);
		_posixRegisterSample.Rdx = *(ulong*)(registers + offsets[2]);
		_posixRegisterSample.Rbx = *(ulong*)(registers + offsets[3]);
		_posixRegisterSample.Rsp = *(ulong*)(registers + offsets[4]);
		_posixRegisterSample.Rbp = *(ulong*)(registers + offsets[5]);
		_posixRegisterSample.Rsi = *(ulong*)(registers + offsets[6]);
		_posixRegisterSample.Rdi = *(ulong*)(registers + offsets[7]);
		_posixRegisterSample.R8 = *(ulong*)(registers + offsets[8]);
		_posixRegisterSample.R9 = *(ulong*)(registers + offsets[9]);
		_posixRegisterSample.R10 = *(ulong*)(registers + offsets[10]);
		_posixRegisterSample.R11 = *(ulong*)(registers + offsets[11]);
		_posixRegisterSample.R12 = *(ulong*)(registers + offsets[12]);
		_posixRegisterSample.R13 = *(ulong*)(registers + offsets[13]);
		_posixRegisterSample.R14 = *(ulong*)(registers + offsets[14]);
		_posixRegisterSample.R15 = *(ulong*)(registers + offsets[15]);
		_posixRegisterSample.Rip = *(ulong*)(registers + offsets[16]);
		Interlocked.Increment(ref _posixSampleSequence);
	}

	[UnmanagedCallersOnly]
	private static void HandlePosixSignal(int signal, nint siginfo, nint ucontext)
	{
		if (_posixSignalHandlerDepth > 0)
		{
			// A fault inside our own fault handler (diagnostics touched an
			// unmapped address): restore the default action and return so the
			// re-executed instruction terminates the process.
			RestoreDefaultPosixAction(signal);
			return;
		}

		_posixSignalHandlerDepth++;
		if (_perfSignalCounter)
		{
			var n = Interlocked.Increment(ref _perfSignalCount);
			if (n % 100000 == 0)
			{
				Console.Error.WriteLine($"[PERF][MEM] posix_faults={n}");
			}
		}
		try
		{
			// Guest-image write tracking runs first: it only needs the fault
			// address (safe for host and guest threads alike) and must resume
			// the faulting write immediately after restoring write access.
			if (signal != PosixSigIll &&
				siginfo != 0 &&
				SharpEmu.HLE.GuestImageWriteTracker.TryHandleWriteFault(
					*(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset)))
			{
				return;
			}

			if (TryHandlePosixFault(signal, siginfo, ucontext))
			{
				return;
			}
		}
		catch
		{
			// A managed exception must never unwind out of a signal frame.
		}
		finally
		{
			_posixSignalHandlerDepth--;
		}

		ChainPreviousPosixAction(signal, siginfo, ucontext);
	}

	private static bool TryHandlePosixFault(int signal, nint siginfo, nint ucontext)
	{
		byte* registers = GetPosixRegisterBase(ucontext);
		if (registers == null)
		{
			return false;
		}

		byte* contextRecord = stackalloc byte[Win64ContextSize];
		new Span<byte>(contextRecord, Win64ContextSize).Clear();
		int[] offsets = PosixRegisterOffsets;
		for (int i = 0; i < offsets.Length; i++)
		{
			WriteCtxU64(contextRecord, CTX_RAX + i * 8, *(ulong*)(registers + offsets[i]));
		}

		EXCEPTION_RECORD record = default;
		record.ExceptionAddress = (void*)ReadCtxU64(contextRecord, CTX_RIP);
		if (signal == PosixSigIll)
		{
			record.ExceptionCode = 3221225501u;
		}
		else
		{
			ulong faultAddress = GetPosixFaultAddress(siginfo, registers);
			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			record.ExceptionInformation[0] = GetPosixAccessType(registers, faultAddress, ReadCtxU64(contextRecord, CTX_RIP));
			record.ExceptionInformation[1] = faultAddress;
		}

		EXCEPTION_POINTERS pointers;
		pointers.ExceptionRecord = &record;
		pointers.ContextRecord = contextRecord;

		int traceIndex = _posixSignalWarmup ? 0 : Interlocked.Increment(ref _posixSignalTraceCount);
		bool traceSignal = traceIndex > 0 && (traceIndex <= 16 || traceIndex % 1024 == 0 ||
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POSIX_SIGNALS"), "1", StringComparison.Ordinal));
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: sig={signal} rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16} " +
				$"fault=0x{record.ExceptionInformation[1]:X16} access={record.ExceptionInformation[0]} rsp=0x{ReadCtxU64(contextRecord, CTX_RSP):X16}");
			Console.Error.Flush();
		}

		// Sentinel recovery runs first: on Windows both vectored handlers see
		// every fault anyway, and recovering here avoids dumping the full
		// VectoredHandler diagnostics for each recoverable trap.
		int disposition = 0;
		if (_posixRawRecoveryEnabled)
		{
			disposition = TryRecoverUnresolvedSentinel(&pointers);
		}
		if (disposition != -1 && !_posixSignalWarmup && _posixSignalBackend is { } backend)
		{
			disposition = backend.VectoredHandler(&pointers);
		}
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: recovered={disposition == -1} new_rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16}");
			Console.Error.Flush();
		}
		if (disposition != -1 && !_posixSignalWarmup)
		{
			return false;
		}

		for (int i = 0; i < offsets.Length; i++)
		{
			*(ulong*)(registers + offsets[i]) = ReadCtxU64(contextRecord, CTX_RAX + i * 8);
		}
		return true;
	}

	private static byte* GetPosixRegisterBase(nint ucontext)
	{
		if (ucontext == 0)
		{
			return null;
		}

		if (OperatingSystem.IsMacOS())
		{
			return *(byte**)((byte*)ucontext + DarwinUcontextMcontextOffset);
		}

		return (byte*)ucontext + LinuxUcontextGregsOffset;
	}

	private static ulong GetPosixFaultAddress(nint siginfo, byte* registers)
	{
		ulong address = siginfo != 0 ? *(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset) : 0;
		if (address == 0 && OperatingSystem.IsMacOS())
		{
			address = *(ulong*)(registers + DarwinMcontextFaultAddressOffset);
		}

		return address;
	}

	private static ulong GetPosixAccessType(byte* registers, ulong faultAddress, ulong rip)
	{
		// x86 page-fault error code: bit 1 = write access, bit 4 = instruction
		// fetch. Fall back to comparing the fault address against RIP when
		// the error code is not populated (e.g. under Rosetta 2 translation).
		ulong error = OperatingSystem.IsMacOS()
			? *(uint*)(registers + DarwinMcontextErrOffset)
			: *(ulong*)(registers + LinuxGregsErrOffset);
		if ((error & 0x10) != 0)
		{
			return 8;
		}
		if ((error & 0x2) != 0)
		{
			return 1;
		}

		return faultAddress != 0 && faultAddress == rip ? 8u : 0u;
	}

	private static void RestoreDefaultPosixAction(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		_ = sigaction(signal, action, null);
	}

	private static void ChainPreviousPosixAction(int signal, nint siginfo, nint ucontext)
	{
		byte* previous = (uint)signal < (uint)_posixPreviousActions.Length
			? (byte*)_posixPreviousActions[signal]
			: null;
		nint handler = previous != null ? *(nint*)previous : 0;
		if (handler == 0)
		{
			// SIG_DFL (or nothing saved): reinstate the default action and
			// return, so re-executing the faulting instruction terminates the
			// process with the original fault context intact.
			RestoreDefaultPosixAction(signal);
			return;
		}
		if (handler == 1)
		{
			// SIG_IGN
			return;
		}

		int flags = *(int*)(previous + PosixSigactionFlagsOffset);
		if ((flags & PosixSaSigInfo) != 0)
		{
			((delegate* unmanaged<int, nint, nint, void>)handler)(signal, siginfo, ucontext);
		}
		else
		{
			((delegate* unmanaged<int, void>)handler)(signal);
		}
	}

	[DllImport("libc", SetLastError = true)]
	private static extern int sigaction(int signum, void* act, void* oldact);

	[DllImport("libc")]
	private static extern nint pthread_self();

	[DllImport("libc")]
	private static extern int pthread_kill(nint thread, int signal);

	[DllImport("libc")]
	private static extern uint pthread_mach_thread_np(nint thread);

	[DllImport("libc")]
	private static extern int thread_suspend(uint targetThread);

	[DllImport("libc")]
	private static extern int thread_resume(uint targetThread);

	[DllImport("libc")]
	private static extern int thread_get_state(uint targetThread, int flavor, ulong* state, ref uint count);
}
