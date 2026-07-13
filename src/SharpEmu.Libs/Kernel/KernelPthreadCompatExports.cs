// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Diagnostics.CodeAnalysis;

namespace SharpEmu.Libs.Kernel;

public static class KernelPthreadCompatExports
{
    private const int MutexTypeErrorCheck = 1;
    private const int MutexTypeRecursive = 2;
    private const int MutexTypeNormal = 3;
    private const int MutexTypeAdaptiveNp = 4;
    private const ulong StaticAdaptiveMutexInitializer = 1;
    private const int MutexObjectSize = 0x100;
    private const int MutexAttrObjectSize = 0x40;
    private const int CondObjectSize = 0x100;
    private const int PthreadOnceUninitialized = 0;
    private const int PthreadOnceInProgress = 1;
    private const int PthreadOnceDone = 2;

    private static readonly object _stateGate = new();
    private static readonly ConcurrentDictionary<ulong, PthreadMutexState> _mutexStates = new();
    private static readonly Dictionary<ulong, PthreadMutexAttrState> _mutexAttrStates = new();
    private static readonly Dictionary<ulong, PthreadCondState> _condStates = new();
    private static readonly Dictionary<ulong, object> _onceGates = new();
    private static readonly HashSet<ulong> _condAttrStates = new();
    private static readonly bool _tracePthreads =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREADS"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadConds =
        _tracePthreads ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_CONDS"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadTimedWaitDeadlines =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_TIMEDWAIT"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadCondCallsites =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_COND_CALLSITES"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadCondActivity =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_COND_ACTIVITY"), "1", StringComparison.Ordinal);
    private static readonly HashSet<ulong>? _tracePthreadMutexFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_MUTEX_FILTER"));
    private static readonly HashSet<ulong>? _tracePthreadCondFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_COND_FILTER"));
    // FEvent flag watcher: uncapped (rate-limited) dump of the UE FEvent object
    // triggered-flag region on every wait/broadcast/signal of a filtered cond.
    // Purpose-built for the Silent Hill RHI stall: observe whether a broadcast
    // that fails to advance the RHI thread actually set the object's flag.
    private static readonly string? _watchFEventRaw =
        Environment.GetEnvironmentVariable("SHARPEMU_WATCH_FEVENT");
    // "auto" watches every cond touched by the render/RHI/interrupt threads, so
    // the run-varying RHI FEvent cond need not be known in advance.
    private static readonly bool _watchFEventAuto =
        string.Equals(_watchFEventRaw, "auto", StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<ulong>? _watchFEventCondFilter =
        _watchFEventAuto ? null : ParseTraceAddressFilter(_watchFEventRaw);
    private static long _watchFEventCount;
    // Per-cond wait/signal balance tracker. A cond whose waits keep growing while
    // signals stall is the deadlock's unsatisfied wait. Dumped periodically so
    // the culprit is obvious without hand-diffing millions of lines.
    private sealed class CondBalance
    {
        public long Waits;
        public long Signals;
        public long LastWaitCount;
        public long LastSignalCount;
        public string LastWaitThread = "";
        public string LastSignalThread = "";
    }
    private static readonly ConcurrentDictionary<ulong, CondBalance> _condBalance =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_COND_BALANCE"), "1", StringComparison.Ordinal)
            ? new ConcurrentDictionary<ulong, CondBalance>()
            : null!;
    private static long _condBalanceLastDumpTicks;

    private static void RecordCondBalance(ulong condAddress, string operation, string threadName, bool timed = false)
    {
        if (_condBalance is null)
        {
            return;
        }

        var entry = _condBalance.GetOrAdd(condAddress, static _ => new CondBalance());
        if (operation is "signal" or "broadcast")
        {
            Interlocked.Increment(ref entry.Signals);
            entry.LastSignalThread = threadName;
        }
        else
        {
            Interlocked.Increment(ref entry.Waits);
            // A plain (non-timed) wait blocks until signalled; a timed wait polls
            // and self-releases, so only plain waits indicate a real starve.
            entry.LastWaitThread = timed ? threadName + "(timed)" : threadName;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _condBalanceLastDumpTicks);
        if (now - last < System.Diagnostics.Stopwatch.Frequency * 15 ||
            Interlocked.CompareExchange(ref _condBalanceLastDumpTicks, now, last) != last)
        {
            return;
        }

        // Report conds whose wait count advanced since the last window but whose
        // signal count did not — i.e. a thread parked with no producer waking it.
        foreach (var (cond, bal) in _condBalance)
        {
            var waits = Interlocked.Read(ref bal.Waits);
            var signals = Interlocked.Read(ref bal.Signals);
            var waitDelta = waits - bal.LastWaitCount;
            var signalDelta = signals - bal.LastSignalCount;
            bal.LastWaitCount = waits;
            bal.LastSignalCount = signals;
            if (waitDelta > 0 && signalDelta == 0 && waits - signals > 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][DIAG] cond_balance STARVED cond=0x{cond:X16} " +
                    $"waits={waits} signals={signals} wait_delta=+{waitDelta} " +
                    $"last_waiter='{bal.LastWaitThread}' last_signaler='{bal.LastSignalThread}'");
            }
        }
    }
    private static readonly bool _enableMutexLockBlocking =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_MUTEX_LOCK_BLOCKING"), "1", StringComparison.Ordinal);
    private static readonly bool _enableCondSignalLatch =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_COND_LATCH"), "1", StringComparison.Ordinal);
    private static readonly HashSet<ulong>? _condSignalLatchFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_COND_LATCH_FILTER"));
    private static readonly string[] _condSignalLatchThreads = ParseNameFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_COND_LATCH_THREADS"));
    private static readonly ConcurrentDictionary<ulong, int> _tracePthreadCondDumpCounts = new();
    private static readonly ConcurrentDictionary<ulong, int> _tracePthreadCondCallsiteCounts = new();
    private static readonly ConcurrentDictionary<ulong, int> _tracePthreadCondSignalCallsiteCounts = new();
    private static readonly ConcurrentDictionary<ulong, PthreadCondActivity> _pthreadCondActivity = new();
    private static readonly TimeSpan? _condCompatibilityRecheck = ParsePositiveMilliseconds(
        Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_COND_RECHECK_MS"));
    private static readonly HashSet<ulong>? _condCompatibilityRecheckFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_COND_RECHECK_FILTER"));
    private static long _nextSynchronizationWaiterId;
    private static int _traceTimedWaitDeadlineCount;

    private sealed class PthreadMutexState
    {
        public ulong OwnerThreadId { get; set; }
        public int RecursionCount { get; set; }
        public int Type { get; set; } = MutexTypeErrorCheck;
        public int Protocol { get; set; }
        public LinkedList<PthreadMutexWaiter> Waiters { get; } = new();
    }

    private sealed class PthreadMutexWaiter
    {
        public required ulong ThreadId { get; init; }
        public required string WakeKey { get; init; }
        public required bool Cooperative { get; init; }
        public LinkedListNode<PthreadMutexWaiter>? Node { get; set; }
        public int Granted;
    }

    private sealed class PthreadCondState
    {
        public object SyncRoot { get; } = new();
        public LinkedList<PthreadCondWaiter> Waiters { get; } = new();
        public int PendingSignals { get; set; }
        public bool LatchMissedSignals { get; set; }

        public bool TryConsumePendingSignal()
        {
            if (PendingSignals == 0)

            {
                return false;
            }

            PendingSignals = 0;

            return true;
        }
    }

    private sealed class PthreadCondWaiter
    {
        public required ulong ThreadId { get; init; }
        public required PthreadMutexState MutexState { get; init; }
        public required string WakeKey { get; init; }
        public required bool Cooperative { get; init; }
        public ulong CondAddress { get; init; }
        public ulong ResolvedCondAddress { get; init; }
        public ulong MutexAddress { get; init; }
        public ulong ImportReturnRip { get; init; }
        public bool CompatibilityRecheck { get; init; }
        public bool PosixErrors { get; init; }
        public LinkedListNode<PthreadCondWaiter>? Node { get; set; }
        public PthreadMutexWaiter? MutexWaiter { get; set; }
        public Timer? TimeoutTimer { get; set; }
        // 0 = waiting, 1 = signaled, 2 = timed out.
        public int CompletionState { get; set; }
    }

    private sealed class PthreadCondActivity
    {
        public long WaitCount;
        public long TimedWaitCount;
        public long SignalCount;
        public long BroadcastCount;
        public long RecheckCount;
    }

    private readonly record struct PthreadMutexAttrState(int Type, int Protocol);

    static KernelPthreadCompatExports()
    {
        RunSynchronizationSelfChecks();
    }

    [SysAbiExport(
        Nid = "aI+OeCz8xrQ",
        ExportName = "scePthreadSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSelf(CpuContext ctx)
    {
        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        ctx[CpuRegister.Rax] = currentThreadHandle;
        TracePthreadSelf(ctx, currentThreadHandle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EotR8a3ASf4",
        ExportName = "pthread_self",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSelf(CpuContext ctx) => PthreadSelf(ctx);

    [SysAbiExport(
        Nid = "3PtV6p3QNX4",
        ExportName = "scePthreadEqual",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadEqual(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = left == right ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "7Xl257M4VNI",
        ExportName = "pthread_equal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixPthreadEqual(CpuContext ctx) => PthreadEqual(ctx);

    [SysAbiExport(
        Nid = "T72hz6ffq08",
        ExportName = "scePthreadYield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadYield(CpuContext ctx)
    {
        _ = ctx;
        Thread.Yield();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GBUY7ywdULE",
        ExportName = "scePthreadRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRename(CpuContext ctx)
    {
        if (_tracePthreads)
        {
            var nameAddress = ctx[CpuRegister.Rsi];
            Span<byte> nameBytes = stackalloc byte[64];
            var name = "<unreadable>";
            if (nameAddress != 0 && ctx.Memory.TryRead(nameAddress, nameBytes))
            {
                var length = nameBytes.IndexOf((byte)0);
                name = System.Text.Encoding.UTF8.GetString(length >= 0 ? nameBytes[..length] : nameBytes);
            }
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread.rename thread=0x{ctx[CpuRegister.Rdi]:X16} name=\"{name}\"");
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EI-5-jlq2dE",
        ExportName = "scePthreadGetthreadid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetthreadid(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "3eqs37G74-s",
        ExportName = "pthread_getthreadid_np",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadGetthreadidNp(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "cmo1RIYva9o",
        ExportName = "scePthreadMutexInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "2Of0f+3mhhE",
        ExportName = "scePthreadMutexDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "9UK1vLZQft4",
        ExportName = "scePthreadMutexLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "upoVrzMHFeE",
        ExportName = "scePthreadMutexTrylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "tn3VlD0hG60",
        ExportName = "scePthreadMutexUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    [SysAbiExport(
        Nid = "ttHNfU+qDBU",
        ExportName = "pthread_mutex_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "ltCfaGr2JGE",
        ExportName = "pthread_mutex_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "7H0iTOciTLo",
        ExportName = "pthread_mutex_lock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "K-jXhbt2gn4",
        ExportName = "pthread_mutex_trylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "2Z+PpY6CaJg",
        ExportName = "pthread_mutex_unlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    private static int PthreadGetthreadidCore(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = KernelPthreadState.GetCurrentThreadUniqueId();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "F8bUHwAG284",
        ExportName = "scePthreadMutexattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "smWEktiyyG0",
        ExportName = "scePthreadMutexattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "iMp8QpE+XO4",
        ExportName = "scePthreadMutexattrSettype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "1FGvU0i9saQ",
        ExportName = "scePthreadMutexattrSetprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "dQHWEsJtoE4",
        ExportName = "pthread_mutexattr_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "HF7lK46xzjY",
        ExportName = "pthread_mutexattr_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "mDmgMOGVUqg",
        ExportName = "pthread_mutexattr_settype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "5txKfcMUAok",
        ExportName = "pthread_mutexattr_setprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "2Tb92quprl0",
        ExportName = "scePthreadCondInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "0TyVk4MSLt0",
        ExportName = "pthread_cond_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "g+PZd2hiacg",
        ExportName = "scePthreadCondDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondDestroy(CpuContext ctx) => PthreadCondDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "RXXqi4CtF8w",
        ExportName = "pthread_cond_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondDestroy(CpuContext ctx) =>
        PthreadCondDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "WKAXJ4XBPQ4",
        ExportName = "scePthreadCondWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "BmMjYxmew1w",
        ExportName = "scePthreadCondTimedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondTimedwait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: true, timeoutUsec: unchecked((uint)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "kDh-NfxgMtE",
        ExportName = "scePthreadCondSignal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false);

    [SysAbiExport(
        Nid = "JGgj7Uvrl+A",
        ExportName = "scePthreadCondBroadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true);

    [SysAbiExport(
        Nid = "Op8TBGY5KHg",
        ExportName = "pthread_cond_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "27bAgiJmOh0",
        ExportName = "pthread_cond_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondTimedwait(CpuContext ctx)
    {
        var deadlineAddress = ctx[CpuRegister.Rdx];
        if (deadlineAddress == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, deadlineAddress, out var rawSeconds) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                deadlineAddress + sizeof(long),
                out var rawNanoseconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var seconds = unchecked((long)rawSeconds);
        var nanoseconds = unchecked((long)rawNanoseconds);
        if (seconds < 0 || nanoseconds is < 0 or >= 1_000_000_000)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var now = DateTimeOffset.UtcNow;
        var nowSeconds = now.ToUnixTimeSeconds();
        var nowNanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100L;
        var deltaSeconds = seconds - nowSeconds;
        uint timeoutUsec;
        if (deltaSeconds < 0)
        {
            timeoutUsec = 0;
        }
        else if (deltaSeconds > uint.MaxValue / 1_000_000L + 1)
        {
            timeoutUsec = uint.MaxValue;
        }
        else
        {
            var remainingNanoseconds =
                deltaSeconds * 1_000_000_000L + nanoseconds - nowNanoseconds;
            var remainingUsec = remainingNanoseconds <= 0
                ? 0
                : (remainingNanoseconds + 999L) / 1_000L;
            timeoutUsec = (uint)Math.Min(remainingUsec, uint.MaxValue);
        }

        if (_tracePthreadTimedWaitDeadlines && Interlocked.Increment(ref _traceTimedWaitDeadlineCount) <= 16)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread_cond_timedwait_deadline: " +
                $"deadline={seconds}.{nanoseconds:D9} now={nowSeconds}.{nowNanoseconds:D9} " +
                $"timeout_us={timeoutUsec}");
        }

        return PthreadCondWaitCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            timed: true,
            timeoutUsec,
            posixErrors: true);
    }

    [SysAbiExport(
        Nid = "mkx2fVhNMsg",
        ExportName = "pthread_cond_broadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true);

    [SysAbiExport(
        Nid = "2MOy+rUfuhQ",
        ExportName = "pthread_cond_signal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false);

    [SysAbiExport(
        Nid = "m5-2bsNfv7s",
        ExportName = "scePthreadCondattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrInit(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Add(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "waPcxYiR3WA",
        ExportName = "scePthreadCondattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrDestroy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Remove(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "14bOACANTBo",
        ExportName = "scePthreadOnce",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadOnce(CpuContext ctx)
    {
        var onceAddress = ctx[CpuRegister.Rdi];
        var initRoutine = ctx[CpuRegister.Rsi];
        if (onceAddress == 0 || initRoutine == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadInt32(ctx, onceAddress, out var onceValue))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (onceValue == PthreadOnceDone)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var gate = GetPthreadOnceGate(onceAddress);
        var shouldCall = false;
        lock (gate)
        {
            if (!TryReadInt32(ctx, onceAddress, out onceValue))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            while (onceValue == PthreadOnceInProgress)
            {
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(1));
                if (!TryReadInt32(ctx, onceAddress, out onceValue))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (onceValue != PthreadOnceDone)
            {
                if (!TryWriteInt32(ctx, onceAddress, PthreadOnceInProgress))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                shouldCall = true;
            }
        }

        if (shouldCall)
        {
            var scheduler = GuestThreadExecution.Scheduler;
            string? error = null;
            if (scheduler is null ||
                !scheduler.TryCallGuestFunction(ctx, initRoutine, 0, 0, 0, 0, "pthread_once", out error))
            {
                lock (gate)
                {
                    _ = TryWriteInt32(ctx, onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                }

                TracePthreadOnce(onceAddress, initRoutine, "failed", error);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
            }

            lock (gate)
            {
                if (!TryWriteInt32(ctx, onceAddress, PthreadOnceDone))
                {
                    _ = TryWriteInt32(ctx, onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                Monitor.PulseAll(gate);
            }
        }

        TracePthreadOnce(onceAddress, initRoutine, shouldCall ? "call" : "done", null);
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int PthreadMutexInitCore(CpuContext ctx, ulong mutexAddress, ulong attrAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var attr = ResolveMutexAttrState(ctx, attrAddress);
        var state = new PthreadMutexState
        {
            Type = attr.Type,
            Protocol = attr.Protocol,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        if (!InitializeMutexObject(ctx, handle, state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _mutexStates[mutexAddress] = state;
        _mutexStates[handle] = state;

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexDestroyCore(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexHandle(ctx, mutexAddress);
        if (!_mutexStates.TryGetValue(resolvedAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (state)
        {
            if (state.OwnerThreadId != 0 || state.RecursionCount != 0 || state.Waiters.Count != 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            _mutexStates.TryRemove(resolvedAddress, out _);
            if (resolvedAddress != mutexAddress)
            {
                _mutexStates.TryRemove(mutexAddress, out _);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexLockCore(CpuContext ctx, ulong mutexAddress, bool tryOnly)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        var canCooperativelyBlock = _enableMutexLockBlocking &&
            !tryOnly &&
            GuestThreadExecution.IsGuestThread &&
            GuestThreadExecution.TryGetCurrentImportCallFrame(out _);
        PthreadMutexWaiter? waiter = null;
        lock (state)
        {
            if (state.OwnerThreadId == currentThreadId)
            {
                if (state.Type == MutexTypeRecursive)
                {
                    state.RecursionCount++;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                if (state.Type == MutexTypeAdaptiveNp)
                {
                    if (tryOnly)
                    {
                        TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                    }

                    // PS5 runtime wrappers can layer an adaptive lock call over
                    // scePthreadMutexLock for one logical acquisition, followed
                    // by only one unlock. Treat that same-owner duplicate as an
                    // idempotent acquisition: recursive counting would retain
                    // ownership after the matching unlock, while EDEADLK leaks a
                    // spurious initialization failure back into the runtime.
                    TracePthreadMutex(ctx, "lock-idempotent", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                if (state.Type == MutexTypeNormal)
                {
                    // A normal mutex self-lock is not a recursive acquisition.
                    TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                }
                else
                {
                    var ownedResult = tryOnly
                        ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
                        : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, ownedResult);
                    return ownedResult;
                }
            }

            if (state.OwnerThreadId == 0 && state.Waiters.Count == 0)
            {
                state.OwnerThreadId = currentThreadId;
                state.RecursionCount = 1;
                TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (tryOnly)
            {
                TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            waiter = EnqueueMutexWaiterLocked(state, currentThreadId, canCooperativelyBlock);
        }

        if (canCooperativelyBlock && waiter is not null &&
            GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "pthread_mutex_lock",
                waiter.WakeKey,
                () => CompleteBlockedMutexLock(ctx, mutexAddress, resolvedAddress, state, waiter),
                () => TryGrantBlockedMutexLock(ctx, mutexAddress, resolvedAddress, state, waiter)))
        {
            TracePthreadMutex(ctx, "lock-block", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var hostResult = WaitForHostMutexLock(state, waiter!);
        TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, hostResult);
        return hostResult;
    }

    private static int PthreadMutexUnlockCore(CpuContext ctx, ulong mutexAddress, bool requireOwner)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        string? nextWakeKey = null;
        lock (state)
        {
            if (state.RecursionCount <= 0)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            if (requireOwner && state.OwnerThreadId != currentThreadId)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            state.RecursionCount--;
            if (state.RecursionCount == 0)
            {
                state.OwnerThreadId = 0;
                nextWakeKey = state.Waiters.First?.Value.Cooperative == true
                    ? state.Waiters.First.Value.WakeKey
                    : null;
                Monitor.PulseAll(state);
            }
        }

        if (nextWakeKey is not null)
        {
            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(nextWakeKey, 1);
        }

        TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrInitCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, MutexAttrObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var initialState = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        if (!WriteMutexAttrObject(ctx, handle, initialState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            _mutexAttrStates[attrAddress] = initialState;
            _mutexAttrStates[handle] = initialState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, handle))
        {
            lock (_stateGate)
            {
                _mutexAttrStates.Remove(attrAddress);
                _mutexAttrStates.Remove(handle);
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrDestroyCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            _mutexAttrStates.Remove(resolvedAddress);
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates.Remove(attrAddress);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrSettypeCore(CpuContext ctx, ulong attrAddress, int type)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Type = NormalizeMutexType(type) };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static int PthreadMutexattrSetprotocolCore(CpuContext ctx, ulong attrAddress, int protocol)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Protocol = protocol };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static ulong ResolveMutexHandle(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return 0;
        }

        if (_mutexStates.ContainsKey(mutexAddress))
        {
            return mutexAddress;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle) && pointedHandle != 0)
        {
            if (_mutexStates.ContainsKey(pointedHandle))
            {
                return pointedHandle;
            }
        }

        return mutexAddress;
    }

    private static bool TryResolveMutexState(CpuContext ctx, ulong mutexAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (mutexAddress == 0)
        {
            return false;
        }

        if (_mutexStates.TryGetValue(mutexAddress, out state))
        {
            resolvedAddress = mutexAddress;
            return true;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle == StaticAdaptiveMutexInitializer)
        {
            return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeAdaptiveNp, out resolvedAddress, out state);
        }

        if (pointedHandle != 0)
        {
            if (_mutexStates.TryGetValue(pointedHandle, out state))
            {
                _mutexStates.TryAdd(mutexAddress, state);
                resolvedAddress = pointedHandle;
                return true;
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = mutexAddress;
            return false;
        }

        return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeErrorCheck, out resolvedAddress, out state);
    }

    private static ulong ResolveMutexAttrHandle(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return 0;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, attrAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_mutexAttrStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        lock (_stateGate)
        {
            if (_mutexAttrStates.ContainsKey(attrAddress))
            {
                return attrAddress;
            }
        }

        return attrAddress;
    }

    private static PthreadMutexAttrState ResolveMutexAttrState(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            return _mutexAttrStates.TryGetValue(resolvedAddress, out var state)
                ? state
                : new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        }
    }

    private static ulong ResolveCondHandle(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return 0;
        }

        lock (_stateGate)
        {
            if (_condStates.ContainsKey(condAddress))
            {
                return condAddress;
            }
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        return condAddress;
    }

    private static bool TryResolveCondState(CpuContext? ctx, ulong condAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadCondState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (condAddress == 0)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_condStates.TryGetValue(condAddress, out state))
            {
                resolvedAddress = condAddress;
                return true;
            }
        }

        if (ctx is null || !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.TryGetValue(pointedHandle, out state))
                {
                    _condStates[condAddress] = state;
                    resolvedAddress = pointedHandle;
                    return true;
                }
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = condAddress;
            return false;
        }

        var createdState = new PthreadCondState();
        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return false;
        }

        lock (_stateGate)
        {
            _condStates[condAddress] = createdState;
            _condStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static bool TryAllocateOpaqueObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, alignment: 0x10, out address))
        {
            return false;
        }

        Span<byte> initialData = stackalloc byte[size];
        initialData.Clear();
        return ctx.Memory.TryWrite(address, initialData);
    }

    private static bool InitializeMutexObject(CpuContext ctx, ulong address, PthreadMutexState state) =>
        TryWriteUInt32(ctx, address + 0x20, unchecked((uint)state.Type)) &&
        TryWriteUInt32(ctx, address + 0x3C, unchecked((uint)state.Protocol));

    private static bool WriteMutexAttrObject(CpuContext ctx, ulong address, PthreadMutexAttrState state) =>
        TryWriteUInt32(ctx, address, unchecked((uint)state.Type)) &&
        TryWriteUInt32(ctx, address + 4, unchecked((uint)state.Protocol));

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BitConverter.TryWriteBytes(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int PthreadCondInitCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            var state = new PthreadCondState();
            _condStates[condAddress] = state;
            _condStates[handle] = state;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondDestroyCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveCondHandle(ctx, condAddress);
        lock (_stateGate)
        {
            if (!_condStates.TryGetValue(resolvedAddress, out var state))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            lock (state.SyncRoot)
            {
                if (state.Waiters.Count != 0)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                }
            }

            _condStates.Remove(resolvedAddress);
            if (resolvedAddress != condAddress)
            {
                _condStates.Remove(condAddress);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondWaitCore(
        CpuContext ctx,
        ulong condAddress,
        ulong mutexAddress,
        bool timed,
        uint timeoutUsec = 0,
        bool posixErrors = false)
    {
        if (condAddress == 0 || mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(ctx, condAddress, createIfZero: true, out var resolvedCondAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedMutexAddress, out var mutexState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        TracePthreadCondActivity(
            ctx,
            timed ? "wait-timed" : "wait",
            condAddress,
            mutexAddress,
            resolvedCondAddress,
            currentThreadId,
            timed,
            timeoutUsec,
            broadcast: false);
        WatchFEventFlag(ctx, timed ? "wait-timed" : "wait", condAddress);
        if (_condBalance is not null)
        {
            var wn = KernelPthreadState.TryGetThreadIdentity(currentThreadId, out var wid)
                ? wid.Name : "<unknown>";
            RecordCondBalance(condAddress, "wait", wn, timed);
        }
        var latchMissedSignals = ShouldLatchSignal(condAddress, resolvedCondAddress) ||
            ShouldLatchCurrentThread(currentThreadId);
        lock (mutexState)
        {
            if (mutexState.OwnerThreadId != currentThreadId || mutexState.RecursionCount != 1)
            {
                return mutexState.OwnerThreadId == currentThreadId
                    ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }
        }

        var consumedPendingSignal = false;
        lock (state.SyncRoot)
        {
            consumedPendingSignal = state.TryConsumePendingSignal();
        }

        if (consumedPendingSignal)
        {
            TracePthreadCond("wait-wake-pending", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            var unlockResult = PthreadMutexUnlockCore(ctx, mutexAddress, requireOwner: true);
            if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                return unlockResult;
            }

            return PthreadMutexLockCore(ctx, mutexAddress, tryOnly: false);
        }

        var cooperative = GuestThreadExecution.IsGuestThread &&
            GuestThreadExecution.TryGetCurrentImportCallFrame(out _);
        var compatibilityRecheck = !timed &&
            ShouldCompatibilityRecheck(condAddress, resolvedCondAddress);
        var importReturnRip = GuestThreadExecution.TryGetCurrentImportCallFrame(out var importFrame)
            ? importFrame.ReturnRip
            : 0;
        var waiter = new PthreadCondWaiter
        {
            ThreadId = currentThreadId,
            MutexState = mutexState,
            Cooperative = cooperative,
            CondAddress = condAddress,
            ResolvedCondAddress = resolvedCondAddress,
            MutexAddress = mutexAddress,
            ImportReturnRip = importReturnRip,
            CompatibilityRecheck = compatibilityRecheck,
            PosixErrors = posixErrors,
            WakeKey = cooperative
                ? $"pthread_cond_waiter:{Interlocked.Increment(ref _nextSynchronizationWaiterId)}"
                : string.Empty,
        };

        var consumedPendingSignal = false;
        lock (state.SyncRoot)
        {
            if (latchMissedSignals)
            {
                state.LatchMissedSignals = true;
                consumedPendingSignal = state.TryConsumePendingSignal();
            }

            if (!consumedPendingSignal)
            {
                waiter.Node = state.Waiters.AddLast(waiter);
                TracePthreadCond("wait-enter", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                TracePthreadCondCallsite(ctx, condAddress);
                TracePthreadCondGuestState(ctx, "wait-enter", condAddress, mutexAddress, resolvedCondAddress, resolvedMutexAddress);

                var unlockResult = PthreadMutexUnlockCore(ctx, mutexAddress, requireOwner: true);
                if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                {
                    RemoveCondWaiterLocked(state, waiter);
                    TracePthreadCond("wait-unlock-fail", condAddress, mutexAddress, state, timed, unlockResult);
                    return unlockResult;
                }

                // Cooperative guest workers normally remain signal-driven.  A
                // caller can opt into compatibility polling globally or narrow it
                // to known queue conditions with the address filter.
                if (cooperative && (timed || compatibilityRecheck))
                {
                    waiter.TimeoutTimer = new Timer(
                        static callbackState =>
                        {
                            var (condState, condWaiter, isTimed) = ((PthreadCondState, PthreadCondWaiter, bool))callbackState!;
                            CompleteCondWaiter(condState, condWaiter, timedOut: isTimed);
                        },
                        (state, waiter, timed),
                        timed ? GetCondWaitTimeout(timeoutUsec) : _condCompatibilityRecheck!.Value,
                        Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (consumedPendingSignal)
        {
            TracePthreadCond("wait-wake-pending", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            var unlockResult = PthreadMutexUnlockCore(ctx, mutexAddress, requireOwner: true);
            if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                return unlockResult;
            }

            return PthreadMutexLockCore(ctx, mutexAddress, tryOnly: false);
        }

        if (cooperative &&
            GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                timed
                    ? $"pthread_cond_timedwait:0x{condAddress:X16}"
                    : $"pthread_cond_wait:0x{condAddress:X16}",
                waiter.WakeKey,
                () => CompleteBlockedCondWait(ctx, condAddress, mutexAddress, state, waiter),
                () => TryGrantCondWaiterMutex(waiter)))
        {
            TracePthreadCond("wait-block", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Non-guest callers have no resumable CPU continuation. Park only
        // those host-side compatibility callers, preserving the same FIFO
        // mutex reacquisition rules as cooperative guest waiters.
        lock (state.SyncRoot)
        {
            var deadline = timed
                ? GuestThreadExecution.ComputeDeadlineTimestamp(GetCondWaitTimeout(timeoutUsec))
                : long.MaxValue;
            while (waiter.CompletionState == 0)
            {
                if (!timed)
                {
                    if (!compatibilityRecheck)
                    {
                        Monitor.Wait(state.SyncRoot);
                        continue;
                    }

                    if (Monitor.Wait(state.SyncRoot, _condCompatibilityRecheck.GetValueOrDefault()))
                    {
                        continue;
                    }

                    if (CompleteCondWaiterLocked(state, waiter, timedOut: false))
                    {
                        TracePthreadCondCompatibilityRecheck(waiter);
                    }
                    continue;
                }

                var remaining = GetRemainingTimeout(deadline);
                if (remaining <= TimeSpan.Zero || !WaitForMonitorSignal(state.SyncRoot, remaining))
                {
                    CompleteCondWaiterLocked(state, waiter, timedOut: true);
                    break;
                }
            }
        }

        if (waiter.MutexWaiter is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = WaitForHostMutexLock(mutexState, waiter.MutexWaiter);
        var waitResult = waiter.CompletionState == 2
            ? CondTimedOutResult(waiter)
            : (int)OrbisGen2Result.ORBIS_GEN2_OK;
        TracePthreadCond(waiter.CompletionState == 2 ? "wait-exit-timeout" : "wait-exit", condAddress, mutexAddress, state, timed, waitResult);
        return waitResult;
    }

    private static int PthreadCondSignalCore(CpuContext ctx, ulong condAddress, bool broadcast)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(ctx, condAddress, createIfZero: true, out var resolvedCondAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        List<PthreadCondWaiter>? completedWaiters = null;
        lock (state.SyncRoot)
        {
            TracePthreadCondActivity(
                ctx,
                broadcast ? "broadcast" : "signal",
                condAddress,
                mutexAddress: 0,
                resolvedCondAddress,
                KernelPthreadState.GetCurrentThreadHandle(),
                timed: false,
                timeoutUsec: 0,
                broadcast);
            TracePthreadCondSignalCallsite(ctx, condAddress, broadcast);
            TracePthreadCondGuestState(ctx, broadcast ? "broadcast" : "signal", condAddress, 0, resolvedCondAddress, 0);
            WatchFEventFlag(ctx, broadcast ? "broadcast" : "signal", condAddress);
            if (_condBalance is not null)
            {
                var sn = KernelPthreadState.TryGetThreadIdentity(
                    KernelPthreadState.GetCurrentThreadHandle(), out var sid) ? sid.Name : "<unknown>";
                RecordCondBalance(condAddress, broadcast ? "broadcast" : "signal", sn);
            }
            for (var node = state.Waiters.First; node is not null;)
            {
                var next = node.Next;
                var waiter = node.Value;
                if (waiter.CompletionState == 0 && CompleteCondWaiterLocked(state, waiter, timedOut: false))
                {
                    (completedWaiters ??= new List<PthreadCondWaiter>()).Add(waiter);
                    if (!broadcast)
                    {
                        break;
                    }
                }

                node = next;
            }

            if ((ShouldLatchSignal(condAddress, resolvedCondAddress) || state.LatchMissedSignals) &&
                completedWaiters is null)
            {
                // A binary latch is enough to bridge the UE startup race and
                // cannot build an unbounded backlog of stale condition wakes.
                state.PendingSignals = 1;

            }

            TracePthreadCond(broadcast ? "broadcast" : "signal", condAddress, mutexAddress: 0, state, timed: false, (int)OrbisGen2Result.ORBIS_GEN2_OK);

        }

        if (completedWaiters is not null)
        {
            foreach (var waiter in completedWaiters)
            {
                WakeCooperativeWaiter(waiter);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static PthreadMutexWaiter EnqueueMutexWaiterLocked(
        PthreadMutexState state,
        ulong threadId,
        bool cooperative,
        string? wakeKey = null)
    {
        var waiter = new PthreadMutexWaiter
        {
            ThreadId = threadId,
            Cooperative = cooperative,
            WakeKey = cooperative
                ? wakeKey ?? $"pthread_mutex_waiter:{Interlocked.Increment(ref _nextSynchronizationWaiterId)}"
                : string.Empty,
        };
        waiter.Node = state.Waiters.AddLast(waiter);
        return waiter;
    }

    [Conditional("DEBUG")]
    private static void RunSynchronizationSelfChecks()
    {
        var mutex = new PthreadMutexState();
        PthreadMutexWaiter first;
        PthreadMutexWaiter second;
        lock (mutex)
        {
            first = EnqueueMutexWaiterLocked(mutex, 0x101, cooperative: false);
            second = EnqueueMutexWaiterLocked(mutex, 0x202, cooperative: false);
            Debug.Assert(!TryGrantMutexWaiterLocked(mutex, second), "A mutex waiter bypassed FIFO order.");
            Debug.Assert(TryGrantMutexWaiterLocked(mutex, first), "The FIFO mutex head was not granted.");
            Debug.Assert(mutex.OwnerThreadId == first.ThreadId && mutex.RecursionCount == 1, "Mutex ownership was not transferred atomically.");
            mutex.OwnerThreadId = 0;
            mutex.RecursionCount = 0;
            Debug.Assert(TryGrantMutexWaiterLocked(mutex, second), "The second mutex waiter was not granted after release.");
        }

        var cond = new PthreadCondState();
        var condMutex = new PthreadMutexState();
        var condWaiter = new PthreadCondWaiter
        {
            ThreadId = 0x303,
            MutexState = condMutex,
            WakeKey = string.Empty,
            Cooperative = false,
        };
        lock (cond.SyncRoot)
        {
            condWaiter.Node = cond.Waiters.AddLast(condWaiter);
            Debug.Assert(CompleteCondWaiterLocked(cond, condWaiter, timedOut: false), "A condition waiter was not completed.");
            Debug.Assert(cond.Waiters.Count == 0 && condWaiter.MutexWaiter is not null, "Condition completion did not atomically queue mutex reacquisition.");
        }
    }

    private static bool TryGrantMutexWaiterLocked(PthreadMutexState state, PthreadMutexWaiter waiter)
    {
        if (Volatile.Read(ref waiter.Granted) != 0)
        {
            return true;
        }

        if (state.OwnerThreadId != 0 ||
            waiter.Node is null ||
            !ReferenceEquals(state.Waiters.First, waiter.Node))
        {
            return false;
        }

        state.Waiters.Remove(waiter.Node);
        waiter.Node = null;
        state.OwnerThreadId = waiter.ThreadId;
        state.RecursionCount = 1;
        Volatile.Write(ref waiter.Granted, 1);
        Monitor.PulseAll(state);
        return true;
    }

    private static int WaitForHostMutexLock(PthreadMutexState state, PthreadMutexWaiter waiter)
    {
        lock (state)
        {
            while (!TryGrantMutexWaiterLocked(state, waiter))
            {
                Monitor.Wait(state);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGrantBlockedMutexLock(
        CpuContext ctx,
        ulong mutexAddress,
        ulong resolvedAddress,
        PthreadMutexState state,
        PthreadMutexWaiter waiter)
    {
        var granted = false;
        lock (state)
        {
            granted = TryGrantMutexWaiterLocked(state, waiter);
        }

        TracePthreadMutex(
            ctx,
            granted ? "lock-reserve" : "lock-reserve-busy",
            mutexAddress,
            resolvedAddress,
            state,
            waiter.ThreadId,
            granted ? (int)OrbisGen2Result.ORBIS_GEN2_OK : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
        return granted;
    }

    private static int CompleteBlockedMutexLock(
        CpuContext ctx,
        ulong mutexAddress,
        ulong resolvedAddress,
        PthreadMutexState state,
        PthreadMutexWaiter waiter)
    {
        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        if (Volatile.Read(ref waiter.Granted) == 1)
        {
            TracePthreadMutex(ctx, "lock-resume", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        TracePthreadMutex(ctx, "lock-resume-ungranted", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
    }

    private static bool CompleteCondWaiterLocked(
        PthreadCondState state,
        PthreadCondWaiter waiter,
        bool timedOut)
    {
        if (waiter.CompletionState != 0)
        {
            return false;
        }

        waiter.CompletionState = timedOut ? 2 : 1;
        RemoveCondWaiterLocked(state, waiter);
        waiter.TimeoutTimer?.Dispose();
        waiter.TimeoutTimer = null;

        lock (waiter.MutexState)
        {
            waiter.MutexWaiter = EnqueueMutexWaiterLocked(
                waiter.MutexState,
                waiter.ThreadId,
                waiter.Cooperative,
                waiter.WakeKey);
        }

        Monitor.PulseAll(state.SyncRoot);
        return true;
    }

    private static void CompleteCondWaiter(
        PthreadCondState state,
        PthreadCondWaiter waiter,
        bool timedOut)
    {
        var completed = false;
        lock (state.SyncRoot)
        {
            completed = CompleteCondWaiterLocked(state, waiter, timedOut);
        }

        if (completed)
        {
            if (waiter.CompatibilityRecheck && !timedOut)
            {
                TracePthreadCondCompatibilityRecheck(waiter);
            }

            WakeCooperativeWaiter(waiter);
        }
    }

    private static void RemoveCondWaiterLocked(PthreadCondState state, PthreadCondWaiter waiter)
    {
        if (waiter.Node is not null)
        {
            state.Waiters.Remove(waiter.Node);
            waiter.Node = null;
        }
    }

    private static bool TryGrantCondWaiterMutex(PthreadCondWaiter waiter)
    {
        var mutexWaiter = waiter.MutexWaiter;
        if (waiter.CompletionState == 0 || mutexWaiter is null)
        {
            return false;
        }

        lock (waiter.MutexState)
        {
            return TryGrantMutexWaiterLocked(waiter.MutexState, mutexWaiter);
        }
    }

    private static int CompleteBlockedCondWait(
        CpuContext ctx,
        ulong condAddress,
        ulong mutexAddress,
        PthreadCondState state,
        PthreadCondWaiter waiter)
    {
        waiter.TimeoutTimer?.Dispose();
        waiter.TimeoutTimer = null;
        var result = waiter.MutexWaiter is not null &&
            Volatile.Read(ref waiter.MutexWaiter.Granted) == 1
                ? (waiter.CompletionState == 2
                    ? CondTimedOutResult(waiter)
                    : (int)OrbisGen2Result.ORBIS_GEN2_OK)
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        TracePthreadCond(
            waiter.CompletionState == 2 ? "wait-resume-timeout" : "wait-resume",
            condAddress,
            mutexAddress,
            state,
            waiter.CompletionState == 2,
            result);
        _ = ctx;
        return result;
    }

    private static int CondTimedOutResult(PthreadCondWaiter waiter) =>
        waiter.PosixErrors
            ? 60 // ETIMEDOUT on Orbis/FreeBSD; pthread APIs return errno directly.
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

    private static void WakeCooperativeWaiter(PthreadCondWaiter waiter)
    {
        if (waiter.Cooperative)
        {
            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(waiter.WakeKey, 1);
        }
    }

    private static TimeSpan GetCondWaitTimeout(uint timeoutUsec)
    {
        if (timeoutUsec == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks((long)timeoutUsec * 10L);
    }

    private static TimeSpan GetRemainingTimeout(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private static bool WaitForMonitorSignal(object syncRoot, TimeSpan timeout)
    {
        // Monitor.Wait(TimeSpan) has millisecond host resolution.  Positive
        // sub-millisecond guest deadlines otherwise collapse into a zero-time
        // poll and can execute millions of timed waits before the wall clock
        // reaches the absolute deadline.
        var timeoutMilliseconds = (int)Math.Min(
            int.MaxValue,
            Math.Max(1D, Math.Ceiling(timeout.TotalMilliseconds)));
        return Monitor.Wait(syncRoot, timeoutMilliseconds);
    }

    private static TimeSpan? ParsePositiveMilliseconds(string? value)
    {
        return int.TryParse(value, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    private static bool ShouldCompatibilityRecheck(ulong condAddress, ulong resolvedCondAddress) =>
        _condCompatibilityRecheck.HasValue &&
        (_condCompatibilityRecheckFilter is null ||
         MatchesTraceAddressFilter(_condCompatibilityRecheckFilter, condAddress) ||
         MatchesTraceAddressFilter(_condCompatibilityRecheckFilter, resolvedCondAddress));

    private static bool ShouldLatchSignal(ulong condAddress, ulong resolvedCondAddress) =>
        _enableCondSignalLatch &&
        (_condSignalLatchFilter is null ||
         MatchesTraceAddressFilter(_condSignalLatchFilter, condAddress) ||
         MatchesTraceAddressFilter(_condSignalLatchFilter, resolvedCondAddress));

    private static bool ShouldLatchCurrentThread(ulong currentThreadId)
    {
        if (!_enableCondSignalLatch ||
            _condSignalLatchThreads.Length == 0 ||
            !KernelPthreadState.TryGetThreadIdentity(currentThreadId, out var identity))
        {
            return false;
        }

        foreach (var prefix in _condSignalLatchThreads)
        {
            if (identity.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int NormalizeMutexType(int type)
    {
        return type switch
        {
            0 => MutexTypeErrorCheck,
            1 => MutexTypeErrorCheck,
            2 => MutexTypeRecursive,
            3 => MutexTypeNormal,
            4 => MutexTypeAdaptiveNp,
            _ => MutexTypeErrorCheck,
        };
    }

    private static object GetPthreadOnceGate(ulong onceAddress)
    {
        lock (_stateGate)
        {
            if (!_onceGates.TryGetValue(onceAddress, out var gate))
            {
                gate = new object();
                _onceGates[onceAddress] = gate;
            }

            return gate;
        }
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static bool TryReadInt32(CpuContext ctx, ulong address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool CreateImplicitMutexState(CpuContext ctx, ulong mutexAddress, int type, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        var createdState = new PthreadMutexState
        {
            Type = type,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            resolvedAddress = 0;
            state = null;
            return false;
        }
        if (!InitializeMutexObject(ctx, handle, createdState))
        {
            resolvedAddress = 0;
            state = null;
            return false;
        }

        lock (_stateGate)
        {
            if (_mutexStates.TryGetValue(mutexAddress, out state))
            {
                resolvedAddress = mutexAddress;
                return true;
            }

            if (_mutexStates.TryGetValue(handle, out state))
            {
                resolvedAddress = handle;
                return true;
            }

            _mutexStates[mutexAddress] = createdState;
            _mutexStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            resolvedAddress = 0;
            state = null;
            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static void TracePthreadSelf(CpuContext ctx, ulong currentThreadHandle)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadUniqueId();
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_self: stale_rdi=0x{ctx[CpuRegister.Rdi]:X16} thread=0x{currentThreadHandle:X16} tid=0x{currentThreadId:X16}");
    }

    private static void TracePthreadOnce(ulong onceAddress, ulong initRoutine, string operation, string? error)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $" error={error}";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_once_{operation}: once=0x{onceAddress:X16} init=0x{initRoutine:X16}{suffix}");
    }

    private static void TracePthreadMutex(CpuContext ctx, string operation, ulong mutexAddress, ulong resolvedAddress, PthreadMutexState? state, ulong currentThreadId, int result)
    {
        if (!ShouldTracePthreadMutex(mutexAddress, resolvedAddress))
        {
            return;
        }

        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var guestWord0);
        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress + 8, out var guestWord1);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_{operation}: mutex=0x{mutexAddress:X16} resolved=0x{resolvedAddress:X16} " +
            $"guest[0]=0x{guestWord0:X16} guest[8]=0x{guestWord1:X16} " +
            $"current=0x{currentThreadId:X16} owner=0x{(state?.OwnerThreadId ?? 0):X16} " +
            $"recursion={(state?.RecursionCount ?? 0)} type={(state?.Type ?? 0)} result=0x{unchecked((uint)result):X8}");
    }

    private static void TracePthreadCond(string operation, ulong condAddress, ulong mutexAddress, PthreadCondState? state, bool timed, int result)
    {
        if (!ShouldTracePthreadCond(condAddress))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_{operation}: cond=0x{condAddress:X16} mutex=0x{mutexAddress:X16} " +
            $"waiters={(state?.Waiters.Count ?? 0)} pending={(state?.PendingSignals ?? 0)} timed={timed} result=0x{unchecked((uint)result):X8}");
    }

    private static void TracePthreadCondCallsite(CpuContext ctx, ulong condAddress)
    {
        if ((!_tracePthreadCondCallsites && !ShouldTracePthreadCond(condAddress)) ||
            _tracePthreadCondCallsiteCounts.AddOrUpdate(condAddress, 1, static (_, count) => count + 1) >
                (_tracePthreadCondCallsites ? 16 : 2))
        {
            return;
        }

        var importReturn = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0;
        var frames = new List<string>(8);
        var rbp = ctx[CpuRegister.Rbp];
        for (var index = 0; index < 8 && rbp >= 0x10000; index++)
        {
            if (!ctx.TryReadUInt64(rbp, out var nextRbp) ||
                !ctx.TryReadUInt64(rbp + sizeof(ulong), out var returnRip))
            {
                break;
            }

            frames.Add($"0x{returnRip:X16}");
            if (nextRbp <= rbp || nextRbp - rbp > 0x100000)
            {
                break;
            }

            rbp = nextRbp;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_callsite: cond=0x{condAddress:X16} " +
            $"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
            $"thread=0x{KernelPthreadState.GetCurrentThreadHandle():X16} " +
            $"import_ret=0x{importReturn:X16} rbp=0x{ctx[CpuRegister.Rbp]:X16} " +
            $"frames=[{string.Join(',', frames)}]");
    }

    private static void TracePthreadCondActivity(
        CpuContext ctx,
        string operation,
        ulong condAddress,
        ulong mutexAddress,
        ulong resolvedCondAddress,
        ulong currentThreadId,
        bool timed,
        uint timeoutUsec,
        bool broadcast)
    {
        if (!_tracePthreadCondActivity)
        {
            return;
        }

        var activity = _pthreadCondActivity.GetOrAdd(resolvedCondAddress, static _ => new PthreadCondActivity());
        long operationCount;
        if (operation.StartsWith("wait", StringComparison.Ordinal))
        {
            operationCount = Interlocked.Increment(ref activity.WaitCount);
            if (timed)
            {
                Interlocked.Increment(ref activity.TimedWaitCount);
            }
        }
        else if (broadcast)
        {
            operationCount = Interlocked.Increment(ref activity.BroadcastCount);
        }
        else
        {
            operationCount = Interlocked.Increment(ref activity.SignalCount);
        }

        if (!ShouldReportPthreadCondActivity(operationCount))
        {
            return;
        }

        var threadName = KernelPthreadState.TryGetThreadIdentity(currentThreadId, out var identity)
            ? identity.Name
            : "<unknown>";
        var importReturn = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0;
        var frames = new List<string>(8);
        var rbp = ctx[CpuRegister.Rbp];
        for (var index = 0; index < 8 && rbp >= 0x10000; index++)
        {
            if (!ctx.TryReadUInt64(rbp, out var nextRbp) ||
                !ctx.TryReadUInt64(rbp + sizeof(ulong), out var returnRip))
            {
                break;
            }

            frames.Add($"0x{returnRip:X16}");
            if (nextRbp <= rbp || nextRbp - rbp > 0x100000)
            {
                break;
            }

            rbp = nextRbp;
        }

        Console.Error.WriteLine(
            $"[LOADER][DIAG] pthread_cond_activity: op={operation} count={operationCount} " +
            $"cond=0x{condAddress:X16} resolved=0x{resolvedCondAddress:X16} mutex=0x{mutexAddress:X16} " +
            $"thread=0x{currentThreadId:X16} name='{threadName}' timed={timed} timeout_us={timeoutUsec} " +
            $"waits={Interlocked.Read(ref activity.WaitCount)} timed_waits={Interlocked.Read(ref activity.TimedWaitCount)} " +
            $"signals={Interlocked.Read(ref activity.SignalCount)} broadcasts={Interlocked.Read(ref activity.BroadcastCount)} " +
            $"import_ret=0x{importReturn:X16} frames=[{string.Join(',', frames)}]");
    }

    private static bool ShouldReportPthreadCondActivity(long count) =>
        count == 1 || count == 8 || (count >= 32 && (count & (count - 1)) == 0);

    private static void TracePthreadCondCompatibilityRecheck(PthreadCondWaiter waiter)
    {
        if (!_tracePthreadCondActivity)
        {
            return;
        }

        var activity = _pthreadCondActivity.GetOrAdd(
            waiter.ResolvedCondAddress,
            static _ => new PthreadCondActivity());
        var recheckCount = Interlocked.Increment(ref activity.RecheckCount);
        if (!ShouldReportPthreadCondActivity(recheckCount))
        {
            return;
        }

        var threadName = KernelPthreadState.TryGetThreadIdentity(waiter.ThreadId, out var identity)
            ? identity.Name
            : "<unknown>";
        Console.Error.WriteLine(
            $"[LOADER][DIAG] pthread_cond_activity: op=compat-recheck count={recheckCount} " +
            $"cond=0x{waiter.CondAddress:X16} resolved=0x{waiter.ResolvedCondAddress:X16} " +
            $"mutex=0x{waiter.MutexAddress:X16} thread=0x{waiter.ThreadId:X16} name='{threadName}' " +
            $"waits={Interlocked.Read(ref activity.WaitCount)} signals={Interlocked.Read(ref activity.SignalCount)} " +
            $"broadcasts={Interlocked.Read(ref activity.BroadcastCount)} import_ret=0x{waiter.ImportReturnRip:X16}");
    }

    private static void TracePthreadCondSignalCallsite(CpuContext ctx, ulong condAddress, bool broadcast)
    {
        if (!ShouldTracePthreadCond(condAddress) ||
            _tracePthreadCondSignalCallsiteCounts.AddOrUpdate(
                condAddress,
                1,
                static (_, count) => count + 1) > 16)
        {
            return;
        }

        var importReturn = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0;
        var frames = new List<string>(8);
        var rbp = ctx[CpuRegister.Rbp];
        for (var index = 0; index < 8 && rbp >= 0x10000; index++)
        {
            if (!ctx.TryReadUInt64(rbp, out var nextRbp) ||
                !ctx.TryReadUInt64(rbp + sizeof(ulong), out var returnRip))
            {
                break;
            }

            frames.Add($"0x{returnRip:X16}");
            if (nextRbp <= rbp || nextRbp - rbp > 0x100000)
            {
                break;
            }

            rbp = nextRbp;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_signal_callsite: op={(broadcast ? "broadcast" : "signal")} " +
            $"cond=0x{condAddress:X16} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
            $"thread=0x{KernelPthreadState.GetCurrentThreadHandle():X16} " +
            $"import_ret=0x{importReturn:X16} rbp=0x{ctx[CpuRegister.Rbp]:X16} " +
            $"frames=[{string.Join(',', frames)}]");
    }

    // Dumps the FEvent object region [cond-0x20 .. cond+0x30] on each op of a
    // watched cond, tagged with thread name. UE's FPThreadEvent lays out
    // { pthread_mutex_t; pthread_cond_t; bool bInitialized; bool bManualReset;
    //   volatile bool bTriggered; volatile int WaitingThreads; }, so the
    // trigger flag sits a few bytes past the cond pointer passed here. Comparing
    // the flag across a wait (RHI) and the broadcasts that fail to wake it shows
    // whether the interrupt thread's trigger reached this exact object.
    private static void WatchFEventFlag(
        CpuContext ctx,
        string operation,
        ulong condAddress)
    {
        if (!_watchFEventAuto &&
            (_watchFEventCondFilter is null ||
             _watchFEventCondFilter.Count == 0 ||
             !MatchesTraceAddressFilter(_watchFEventCondFilter, condAddress)))
        {
            return;
        }

        var threadId = KernelPthreadState.GetCurrentThreadHandle();
        var threadName = KernelPthreadState.TryGetThreadIdentity(threadId, out var identity)
            ? identity.Name
            : "<unknown>";

        // Auto mode: the render/RHI/GPU-interrupt threads' own ops (the FEvent
        // hand-off chain that wedges Silent Hill's pre-title screen), PLUS every
        // signal/broadcast from any thread so the waiter's off-set-producer is
        // caught even when it runs on an unexpected thread.
        var isRenderChain = threadName is "RHIThread" or "RenderThread 0"
            or "RenderThread 1" or "AgcInterruptThread" or "AgcSubmissionThread"
            or "Thread-2";
        var isWake = operation is "signal" or "broadcast";
        if (_watchFEventAuto && !isRenderChain && !isWake)
        {
            return;
        }

        // Rate-limit to keep the log bounded while still sampling the stall.
        var count = Interlocked.Increment(ref _watchFEventCount);
        if (count > 4000 && count % 512 != 0)
        {
            return;
        }

        var words = new List<string>(20);
        var start = condAddress >= 0x40 ? condAddress - 0x40 : 0;
        for (var offset = 0UL; offset <= 0x60; offset += sizeof(ulong))
        {
            var address = start + offset;
            words.Add(KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out var value)
                ? $"{(long)(address - condAddress):+#;-#;0}=0x{value:X16}"
                : "????");
        }

        Console.Error.WriteLine(
            $"[LOADER][DIAG] fevent_watch op={operation} cond=0x{condAddress:X16} " +
            $"thread=0x{threadId:X16} name='{threadName}' n={count} obj[{string.Join(',', words)}]");
    }

    private static void TracePthreadCondGuestState(
        CpuContext ctx,
        string operation,
        ulong condAddress,
        ulong mutexAddress,
        ulong resolvedCondAddress,
        ulong resolvedMutexAddress)
    {
        if (!ShouldTracePthreadCond(condAddress) ||
            _tracePthreadCondDumpCounts.AddOrUpdate(condAddress, 1, static (_, count) => count + 1) > 2)
        {
            return;
        }

        var words = new List<string>(24);
        var startAddress = condAddress >= 0x40 ? condAddress - 0x40 : 0;
        for (var offset = 0UL; offset <= 0xB8; offset += sizeof(ulong))
        {
            var address = startAddress + offset;
            words.Add(KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out var value)
                ? $"{address:X16}:{value:X16}"
                : $"{address:X16}:????????????????");
        }

        var wrapperRbp = ctx[CpuRegister.Rbp];
        _ = ctx.TryReadUInt64(wrapperRbp - 0x10, out var savedR14);
        _ = ctx.TryReadUInt64(wrapperRbp - 0x18, out var savedR13);
        _ = ctx.TryReadUInt64(wrapperRbp - 0x20, out var savedR12);

        var cpuR13 = ctx[CpuRegister.R13];
        var cpuR13Words = new List<string>(9);
        if (operation == "wait-enter" && cpuR13 >= 0x10000)
        {
            for (var offset = 0UL; offset <= 0x40; offset += sizeof(ulong))
            {
                var address = cpuR13 + offset;
                cpuR13Words.Add(KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out var value)
                    ? $"{address:X16}:{value:X16}"
                    : $"{address:X16}:????????????????");
            }
        }

        var ownerWords = new List<string>(32);
        if (operation == "wait-enter" && savedR13 >= 0x10000)
        {
            for (var offset = 0UL; offset <= 0xF8; offset += sizeof(ulong))
            {
                var address = savedR13 + offset;
                ownerWords.Add(KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out var value)
                    ? $"{address:X16}:{value:X16}"
                    : $"{address:X16}:????????????????");
            }
        }

        var queueWords = new List<string>(64);
        if (operation == "wait-enter" &&
            savedR13 >= 0x10000 &&
            (savedR14 & 0xFF) == 0)
        {
            var queueIndex = savedR14 >> 8;
            var queueAddress = savedR13 + queueIndex * 0x200 + 0x38;
            for (var offset = 0UL; offset <= 0x1F8; offset += sizeof(ulong))
            {
                var address = queueAddress + offset;
                queueWords.Add(KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out var value)
                    ? $"{address:X16}:{value:X16}"
                    : $"{address:X16}:????????????????");
            }
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_guest_state: op={operation} cond=0x{condAddress:X16} " +
            $"mutex=0x{mutexAddress:X16} resolved_cond=0x{resolvedCondAddress:X16} " +
            $"resolved_mutex=0x{resolvedMutexAddress:X16} saved_r12=0x{savedR12:X16} " +
            $"saved_r13=0x{savedR13:X16} saved_r14=0x{savedR14:X16} " +
            $"cpu_rbx=0x{ctx[CpuRegister.Rbx]:X16} cpu_r12=0x{ctx[CpuRegister.R12]:X16} " +
            $"cpu_r13=0x{cpuR13:X16} cpu_r14=0x{ctx[CpuRegister.R14]:X16} " +
            $"cpu_r15=0x{ctx[CpuRegister.R15]:X16} cpu_r13_words=[{string.Join(',', cpuR13Words)}] " +
            $"words=[{string.Join(',', words)}] owner=[{string.Join(',', ownerWords)}] " +
            $"queue=[{string.Join(',', queueWords)}]");
    }

    private static bool ShouldTracePthread()
    {
        return _tracePthreads;
    }

    private static bool ShouldTracePthreadMutex(ulong mutexAddress, ulong resolvedAddress)
    {
        if (_tracePthreadMutexFilter is null || _tracePthreadMutexFilter.Count == 0)
        {
            return _tracePthreads;
        }

        return MatchesTraceAddressFilter(_tracePthreadMutexFilter, mutexAddress) ||
            MatchesTraceAddressFilter(_tracePthreadMutexFilter, resolvedAddress);
    }

    private static bool ShouldTracePthreadCond(ulong condAddress)
    {
        if (!_tracePthreadConds)
        {
            return false;
        }

        return _tracePthreadCondFilter is null ||
            _tracePthreadCondFilter.Count == 0 ||
            MatchesTraceAddressFilter(_tracePthreadCondFilter, condAddress);
    }

    private static bool MatchesTraceAddressFilter(HashSet<ulong> filter, ulong address)
    {
        foreach (var candidate in filter)
        {
            if (candidate <= ushort.MaxValue
                ? (address & ushort.MaxValue) == candidate
                : address == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<ulong>? ParseTraceAddressFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var addresses = new HashSet<ulong>();
        foreach (var token in filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            normalized = normalized.TrimStart('0');

            if (ulong.TryParse(
                    normalized.Length == 0 ? "0" : normalized,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.Count == 0 ? null : addresses;
    }

    private static string[] ParseNameFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? []
            : filter.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
