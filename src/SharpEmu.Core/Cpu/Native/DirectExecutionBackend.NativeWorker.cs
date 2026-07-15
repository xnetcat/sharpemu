// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// Guest entry stubs have no CLR unwind information. Running them above a
	// managed Thread frame lets a GC suspension observe an invalid mixed stack
	// and eventually fail-fast with ReversePInvokeBadTransition / the
	// UnmanagedCallersOnly audio-thread crash. Keep guest frames on a raw POSIX
	// worker whose run loop contains no managed frame.
	private static readonly bool NativeGuestWorkersDisabled =
		OperatingSystem.IsWindows() ||
		string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS"),
			"1",
			StringComparison.Ordinal);

	private readonly object _nativeWorkerGate = new();
	private readonly List<NativeGuestExecutor> _allNativeWorkers = new();
	private readonly Stack<NativeGuestExecutor> _idleNativeWorkers = new();
	private bool _nativeWorkersDisposed;
	private int _nativeWorkerCreationFailedLogged;

	private unsafe int RunGuestEntryStub(void* entryStub, ulong hostRspSlot)
	{
		NativeGuestExecutor? worker = RentNativeGuestExecutor();
		if (worker is null)
		{
			TlsSetValue(_hostRspSlotTlsIndex, (nint)hostRspSlot);
			return CallNativeEntry(entryStub);
		}

		try
		{
			GuestThreadState? state = _activeGuestThreadState;
			int nativeReturn = worker.Run(
				_activeCpuContext!,
				state,
				GuestThreadExecution.CurrentGuestThreadHandle,
				_activeEntryReturnSentinelRip,
				_activeGuestReturnSlotAddress,
				(nint)hostRspSlot,
				(nint)entryStub,
				state?.AffinityMask ?? 0,
				out bool yieldRequested,
				out string? yieldReason,
				out bool forcedExit);
			_activeGuestThreadYieldRequested = yieldRequested;
			_activeGuestThreadYieldReason = yieldReason;
			_activeForcedGuestExit = forcedExit;
			return nativeReturn;
		}
		finally
		{
			ReturnNativeGuestExecutor(worker);
		}
	}

	private NativeGuestExecutor? RentNativeGuestExecutor()
	{
		if (NativeGuestWorkersDisabled)
		{
			return null;
		}

		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return null;
			}
			if (_idleNativeWorkers.Count != 0)
			{
				return _idleNativeWorkers.Pop();
			}
		}

		NativeGuestExecutor? worker = NativeGuestExecutor.TryCreate(this);
		if (worker is null)
		{
			if (Interlocked.Exchange(ref _nativeWorkerCreationFailedLogged, 1) == 0)
			{
				Console.Error.WriteLine(
					"[LOADER][WARN] Failed to create a raw POSIX guest worker; falling back to inline guest execution.");
			}
			return null;
		}

		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				worker.Dispose();
				return null;
			}
			_allNativeWorkers.Add(worker);
		}
		return worker;
	}

	private void ReturnNativeGuestExecutor(NativeGuestExecutor worker)
	{
		lock (_nativeWorkerGate)
		{
			if (!_nativeWorkersDisposed)
			{
				_idleNativeWorkers.Push(worker);
				return;
			}
		}
		worker.Dispose();
	}

	private void DisposeNativeGuestExecutors()
	{
		NativeGuestExecutor[] workers;
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return;
			}
			_nativeWorkersDisposed = true;
			workers = _allNativeWorkers.ToArray();
			_allNativeWorkers.Clear();
			_idleNativeWorkers.Clear();
		}

		foreach (NativeGuestExecutor worker in workers)
		{
			worker.Dispose();
		}
	}

	private sealed unsafe class NativeGuestExecutor : IDisposable
	{
		private const uint LoopStubSize = 512;
		private const int InterruptedSystemCall = 4;

		private static readonly object LibcGate = new();
		private static nint _libcHandle;
		private static nint _readAddress;
		private static nint _writeAddress;

		private readonly DirectExecutionBackend _backend;
		private GCHandle _selfHandle;
		private void* _controlBlock;
		private void* _loopStub;
		private nint _nativeThread;
		private int _workRead = -1;
		private int _workWrite = -1;
		private int _doneRead = -1;
		private int _doneWrite = -1;
		private bool _disposed;

		private CpuContext? _runContext;
		private GuestThreadState? _runState;
		private ulong _runGuestThreadHandle;
		private ulong _runSentinelRip;
		private ulong _runReturnSlotAddress;
		private nint _runHostRspSlot;
		private nint _runEntryStub;
		private ulong _runAffinityMask;
		private int _runNativeResult;
		private bool _runYieldRequested;
		private string? _runYieldReason;
		private bool _runForcedExit;
		private bool _runPrologueFailed;

		private DirectExecutionBackend? _previousBackend;
		private CpuContext? _previousContext;
		private ulong _previousSentinel;
		private ulong _previousReturnSlot;
		private bool _previousForcedExit;
		private bool _previousYieldRequested;
		private string? _previousYieldReason;
		private GuestThreadState? _previousState;
		private ulong _previousGuestThreadHandle;
		private nint _previousHostRspSlot;
		private int _previousHostThreadId;
		private bool _entered;

		private NativeGuestExecutor(DirectExecutionBackend backend)
		{
			_backend = backend;
		}

		public static NativeGuestExecutor? TryCreate(DirectExecutionBackend backend)
		{
			if (!EnsureLibcExports())
			{
				return null;
			}

			var executor = new NativeGuestExecutor(backend);
			if (!executor.Initialize())
			{
				executor.Dispose();
				return null;
			}
			return executor;
		}

		private static bool EnsureLibcExports()
		{
			if (_readAddress != 0 && _writeAddress != 0)
			{
				return true;
			}

			lock (LibcGate)
			{
				if (_readAddress != 0 && _writeAddress != 0)
				{
					return true;
				}
				try
				{
					string library = OperatingSystem.IsMacOS()
						? "/usr/lib/libSystem.B.dylib"
						: "libc.so.6";
					_libcHandle = NativeLibrary.Load(library);
					_readAddress = NativeLibrary.GetExport(_libcHandle, "read");
					_writeAddress = NativeLibrary.GetExport(_libcHandle, "write");
				}
				catch
				{
					_readAddress = 0;
					_writeAddress = 0;
				}
				return _readAddress != 0 && _writeAddress != 0;
			}
		}

		private bool Initialize()
		{
			int* workPipe = stackalloc int[2];
			int* donePipe = stackalloc int[2];
			if (pipe(workPipe) != 0)
			{
				return false;
			}
			_workRead = workPipe[0];
			_workWrite = workPipe[1];
			if (pipe(donePipe) != 0)
			{
				return false;
			}
			_doneRead = donePipe[0];
			_doneWrite = donePipe[1];

			_selfHandle = GCHandle.Alloc(this);
			_controlBlock = VirtualAlloc(null, 4096, 12288, 4);
			_loopStub = VirtualAlloc(null, LoopStubSize, 12288, 4);
			if (_controlBlock == null || _loopStub == null)
			{
				return false;
			}

			nint prologue = (nint)(delegate* unmanaged<nint, nint>)&RunPrologue;
			nint epilogue = (nint)(delegate* unmanaged<nint, int, void>)&RunEpilogue;
			nint executorHandle = GCHandle.ToIntPtr(_selfHandle);
			byte* code = (byte*)_loopStub;
			int offset = 0;

			void Emit(byte value) => code[offset++] = value;
			void EmitMovRax(ulong value)
			{
				Emit(0x48); Emit(0xB8);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitMovRdi(ulong value)
			{
				Emit(0x48); Emit(0xBF);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitMovRsi(ulong value)
			{
				Emit(0x48); Emit(0xBE);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitMovEdx(uint value)
			{
				Emit(0xBA);
				*(uint*)(code + offset) = value;
				offset += sizeof(uint);
			}
			void EmitCallRax()
			{
				Emit(0xFF); Emit(0xD0);
			}
			void EmitWriteDone()
			{
				EmitMovRdi((uint)_doneWrite);
				EmitMovRsi((ulong)_controlBlock + 8);
				EmitMovEdx(1);
				EmitMovRax((ulong)_writeAddress);
				EmitCallRax();
			}

			// Preserve the System V nonvolatile set expected by pthread's start
			// trampoline and align RSP to 16 bytes before every call.
			Emit(0x53);                                      // push rbx
			Emit(0x55);                                      // push rbp
			Emit(0x41); Emit(0x54);                          // push r12
			Emit(0x41); Emit(0x55);                          // push r13
			Emit(0x41); Emit(0x56);                          // push r14
			Emit(0x41); Emit(0x57);                          // push r15
			Emit(0x48); Emit(0x83); Emit(0xEC); Emit(0x08); // sub rsp,8

			int loopStart = offset;
			EmitMovRdi((uint)_workRead);
			EmitMovRsi((ulong)_controlBlock + 8);
			EmitMovEdx(1);
			EmitMovRax((ulong)_readAddress);
			EmitCallRax();
			Emit(0x83); Emit(0xF8); Emit(0x01);             // cmp eax,1
			Emit(0x0F); Emit(0x85);                         // jne loop
			int readRetryJump = offset;
			offset += sizeof(int);

			EmitMovRax((ulong)_controlBlock);
			Emit(0x83); Emit(0x38); Emit(0x00);             // cmp dword [rax],0
			Emit(0x0F); Emit(0x85);                         // jne stop
			int stopJump = offset;
			offset += sizeof(int);

			EmitMovRdi((ulong)executorHandle);
			EmitMovRax((ulong)prologue);
			EmitCallRax();
			Emit(0x48); Emit(0x85); Emit(0xC0);             // test rax,rax
			Emit(0x0F); Emit(0x84);                         // je skip entry
			int skipEntryJump = offset;
			offset += sizeof(int);
			EmitCallRax();
			int skipEntry = offset;
			Emit(0x89); Emit(0xC6);                         // mov esi,eax
			EmitMovRdi((ulong)executorHandle);
			EmitMovRax((ulong)epilogue);
			EmitCallRax();
			EmitWriteDone();
			Emit(0xE9);                                     // jmp loop
			int loopJump = offset;
			offset += sizeof(int);

			int stop = offset;
			EmitWriteDone();
			Emit(0x31); Emit(0xC0);                         // xor eax,eax
			Emit(0x48); Emit(0x83); Emit(0xC4); Emit(0x08); // add rsp,8
			Emit(0x41); Emit(0x5F);                         // pop r15
			Emit(0x41); Emit(0x5E);                         // pop r14
			Emit(0x41); Emit(0x5D);                         // pop r13
			Emit(0x41); Emit(0x5C);                         // pop r12
			Emit(0x5D);                                     // pop rbp
			Emit(0x5B);                                     // pop rbx
			Emit(0xC3);                                     // ret

			*(int*)(code + readRetryJump) = loopStart - (readRetryJump + sizeof(int));
			*(int*)(code + stopJump) = stop - (stopJump + sizeof(int));
			*(int*)(code + skipEntryJump) = skipEntry - (skipEntryJump + sizeof(int));
			*(int*)(code + loopJump) = loopStart - (loopJump + sizeof(int));

			uint oldProtect = 0;
			if (!VirtualProtect(_loopStub, LoopStubSize, 32, &oldProtect))
			{
				return false;
			}
			FlushInstructionCache(GetCurrentProcess(), _loopStub, LoopStubSize);
			if (pthread_create(out _nativeThread, 0, (nint)_loopStub, 0) != 0)
			{
				_nativeThread = 0;
				return false;
			}

			Console.Error.WriteLine(
				$"[LOADER][INFO] Raw POSIX guest worker created: pthread=0x{(ulong)_nativeThread:X}");
			return true;
		}

		public int Run(
			CpuContext context,
			GuestThreadState? state,
			ulong guestThreadHandle,
			ulong sentinelRip,
			ulong returnSlotAddress,
			nint hostRspSlot,
			nint entryStub,
			ulong affinityMask,
			out bool yieldRequested,
			out string? yieldReason,
			out bool forcedExit)
		{
			_runContext = context;
			_runState = state;
			_runGuestThreadHandle = guestThreadHandle;
			_runSentinelRip = sentinelRip;
			_runReturnSlotAddress = returnSlotAddress;
			_runHostRspSlot = hostRspSlot;
			_runEntryStub = entryStub;
			_runAffinityMask = affinityMask;
			_runPrologueFailed = true;
			_runYieldRequested = false;
			_runYieldReason = null;
			_runForcedExit = false;

			if (!WriteByte(_workWrite) || !ReadByte(_doneRead))
			{
				throw new InvalidOperationException("Raw POSIX guest worker synchronization failed");
			}

			_runContext = null;
			_runState = null;
			yieldRequested = _runYieldRequested;
			yieldReason = _runYieldReason;
			forcedExit = _runForcedExit;
			if (_runPrologueFailed)
			{
				throw new InvalidOperationException("Raw POSIX guest worker failed to bind the run ambient");
			}
			return _runNativeResult;
		}

		[UnmanagedCallersOnly]
		private static nint RunPrologue(nint executorHandle)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				return executor.EnterRun();
			}
			catch (Exception exception)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Raw guest worker prologue failed: {exception.GetType().Name}: {exception.Message}");
				}
				catch
				{
				}
				return 0;
			}
		}

		[UnmanagedCallersOnly]
		private static void RunEpilogue(nint executorHandle, int nativeResult)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				executor.ExitRun(nativeResult);
			}
			catch (Exception exception)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Raw guest worker epilogue failed: {exception.GetType().Name}: {exception.Message}");
				}
				catch
				{
				}
			}
		}

		private nint EnterRun()
		{
			DirectExecutionBackend backend = _backend;
			_previousBackend = _activeExecutionBackend;
			_previousContext = _activeCpuContext;
			_previousSentinel = _activeEntryReturnSentinelRip;
			_previousReturnSlot = _activeGuestReturnSlotAddress;
			_previousForcedExit = _activeForcedGuestExit;
			_previousYieldRequested = _activeGuestThreadYieldRequested;
			_previousYieldReason = _activeGuestThreadYieldReason;
			_previousState = _activeGuestThreadState;
			_previousHostRspSlot = TlsGetValue(backend._hostRspSlotTlsIndex);
			_previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(_runGuestThreadHandle);
			_entered = true;

			_activeExecutionBackend = backend;
			_activeCpuContext = _runContext;
			_activeEntryReturnSentinelRip = _runSentinelRip;
			_activeGuestReturnSlotAddress = _runReturnSlotAddress;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			_activeGuestThreadState = _runState;
			backend.BindTlsBase(_runContext!);
			TlsSetValue(backend._hostRspSlotTlsIndex, _runHostRspSlot);
			if (_runGuestThreadHandle == 0)
			{
				backend.SetPosixRegisterSamplerTargetForCurrentThread();
			}

			if (_runState is { } state)
			{
				_previousHostThreadId = Volatile.Read(ref state.HostThreadId);
				Volatile.Write(ref state.HostThreadId, unchecked((int)GetCurrentThreadId()));
			}
			if (_runAffinityMask != 0)
			{
				backend.ApplyGuestThreadAffinity(_runAffinityMask);
			}
			_runPrologueFailed = false;
			return _runEntryStub;
		}

		private void ExitRun(int nativeResult)
		{
			_runNativeResult = nativeResult;
			_runYieldRequested = _activeGuestThreadYieldRequested;
			_runYieldReason = _activeGuestThreadYieldReason;
			_runForcedExit = _activeForcedGuestExit;
			if (!_entered)
			{
				return;
			}
			_entered = false;

			if (_runState is { } state)
			{
				Volatile.Write(ref state.HostThreadId, _previousHostThreadId);
			}
			TlsSetValue(_backend._hostRspSlotTlsIndex, _previousHostRspSlot);
			GuestThreadExecution.RestoreGuestThread(_previousGuestThreadHandle);
			_activeExecutionBackend = _previousBackend;
			_activeCpuContext = _previousContext;
			_activeEntryReturnSentinelRip = _previousSentinel;
			_activeGuestReturnSlotAddress = _previousReturnSlot;
			_activeForcedGuestExit = _previousForcedExit;
			_activeGuestThreadYieldRequested = _previousYieldRequested;
			_activeGuestThreadYieldReason = _previousYieldReason;
			_activeGuestThreadState = _previousState;
			_previousBackend = null;
			_previousContext = null;
			_previousState = null;
			_previousYieldReason = null;
		}

		private static bool WriteByte(int fileDescriptor)
		{
			byte value = 1;
			while (write(fileDescriptor, &value, 1) != 1)
			{
				if (Marshal.GetLastPInvokeError() != InterruptedSystemCall)
				{
					return false;
				}
			}
			return true;
		}

		private static bool ReadByte(int fileDescriptor)
		{
			byte value;
			while (read(fileDescriptor, &value, 1) != 1)
			{
				if (Marshal.GetLastPInvokeError() != InterruptedSystemCall)
				{
					return false;
				}
			}
			return true;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;

			if (_controlBlock != null)
			{
				*(int*)_controlBlock = 1;
			}
			if (_nativeThread != 0)
			{
				_ = WriteByte(_workWrite);
				_ = ReadByte(_doneRead);
				_ = pthread_join(_nativeThread, null);
				_nativeThread = 0;
			}

			ClosePipe(ref _workRead);
			ClosePipe(ref _workWrite);
			ClosePipe(ref _doneRead);
			ClosePipe(ref _doneWrite);
			if (_loopStub != null)
			{
				VirtualFree(_loopStub, 0, 32768);
				_loopStub = null;
			}
			if (_controlBlock != null)
			{
				VirtualFree(_controlBlock, 0, 32768);
				_controlBlock = null;
			}
			if (_selfHandle.IsAllocated)
			{
				_selfHandle.Free();
			}
		}

		private static void ClosePipe(ref int fileDescriptor)
		{
			if (fileDescriptor >= 0)
			{
				_ = close(fileDescriptor);
				fileDescriptor = -1;
			}
		}

		[DllImport("libc", SetLastError = true)]
		private static extern int pipe(int* fileDescriptors);

		[DllImport("libc", SetLastError = true)]
		private static extern nint read(int fileDescriptor, void* buffer, nuint count);

		[DllImport("libc", SetLastError = true)]
		private static extern nint write(int fileDescriptor, void* buffer, nuint count);

		[DllImport("libc")]
		private static extern int close(int fileDescriptor);

		[DllImport("libc")]
		private static extern int pthread_create(out nint thread, nint attributes, nint startRoutine, nint argument);

		[DllImport("libc")]
		private static extern int pthread_join(nint thread, void** returnValue);
	}
}
