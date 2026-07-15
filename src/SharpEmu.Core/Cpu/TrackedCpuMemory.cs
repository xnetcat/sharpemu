// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class TrackedCpuMemory :
    ICpuMemory,
    ITrackedCpuMemory,
    IGuestMemoryAllocator,
    ICpuMemoryStateKeyProvider
{
    private readonly ICpuMemory _inner;
    private static readonly ulong _watchedWriteAddress = ParseWatchedWriteAddress();
    private static long _watchedWriteCount;

    public TrackedCpuMemory(ICpuMemory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public CpuMemoryAccessFailure? LastFailure { get; private set; }

    public ICpuMemory Inner => _inner;

    public object CpuMemoryStateKey =>
        _inner is ICpuMemoryStateKeyProvider provider
            ? provider.CpuMemoryStateKey
            : _inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        var result = _inner.TryRead(virtualAddress, destination);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, destination.Length, isWrite: false);
        }

        return result;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var watchesAddress = _watchedWriteAddress != 0 &&
            virtualAddress <= _watchedWriteAddress &&
            (ulong)source.Length > _watchedWriteAddress - virtualAddress;
        Span<byte> before = stackalloc byte[sizeof(ulong)];
        var readBefore = watchesAddress && _inner.TryRead(_watchedWriteAddress, before);
        var result = false;
        if (GuestImageWriteTracker.TryBeginManagedWrite(
                virtualAddress,
                (ulong)source.Length,
                out var writeScope))
        {
            using (writeScope)
            {
                result = _inner.TryWrite(virtualAddress, source);
            }
        }
        if (watchesAddress)
        {
            var hit = Interlocked.Increment(ref _watchedWriteCount);
            if (hit <= 32)
            {
                var sourceOffset = checked((int)(_watchedWriteAddress - virtualAddress));
                var sourceLength = Math.Min(sizeof(ulong), source.Length - sourceOffset);
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] guest-write-watch#{hit} " +
                    $"watch=0x{_watchedWriteAddress:X16} write=0x{virtualAddress:X16}+0x{source.Length:X} " +
                    $"before={(readBefore ? Convert.ToHexString(before) : "<unreadable>")} " +
                    $"source={Convert.ToHexString(source.Slice(sourceOffset, sourceLength))} " +
                    $"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
                    $"managed={Environment.CurrentManagedThreadId} result={result}\n{Environment.StackTrace}");
            }
        }
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, source.Length, isWrite: true);
        }

        return result;
    }

    private static ulong ParseWatchedWriteAddress()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GUEST_WRITE");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 0;
        }

        configured = configured.Trim();
        if (configured.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            configured = configured[2..];
        }

        return ulong.TryParse(
            configured,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var address)
                ? address
                : 0;
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        if (_inner is IGuestMemoryAllocator allocator)
        {
            return allocator.TryAllocateGuestMemory(size, alignment, out address);
        }

        address = 0;
        return false;
    }
}
