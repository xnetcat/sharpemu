// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Tracks unsatisfied WAIT_REG_MEM conditions. In the default mode parsing
/// continues so later producer submits can run, while guest-visible effects
/// following the wait remain deferred. The strict diagnostic mode instead
/// suspends and later resumes the rest of the DCB. AgcExports re-checks every
/// waiter against guest memory on each submit (labels are advanced by
/// ReleaseMem / WriteData / DmaData packets, or by direct CPU writes).
///
/// This preserves cross-submit ordering without requiring a strict graphics
/// FIFO to stop before it reaches a producer submitted behind its consumer.
/// </summary>
internal static class GpuWaitRegistry
{
    public struct WaitingDcb
    {
        public ulong CommandBufferAddress;
        public ulong ResumeAddress;
        public uint TotalDwords;
        public uint ResumeOffset;
        public ulong WaitAddress;
        public ulong ReferenceValue;
        public ulong Mask;
        public uint CompareFunction;
        public uint ControlValue;
        public bool Is64Bit;
        public bool IsStandard;
        // The parser continued past this wait, but guest-visible memory effects
        // after it remain queued until the condition becomes true. This lets a
        // later producer submit run without exposing the consumer's completion
        // labels early.
        public bool DeferEffectsOnly;
        public object? Memory;
        public string? QueueName;
        public ulong SubmissionId;
        // Stopwatch timestamp captured at registration. Stale waiters remain
        // registered; this only controls one-shot diagnostics.
        public long RegisteredTicks;
        public bool StaleReported;
        public object? State;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, List<WaitingDcb>> _waiters = new();

    public static int Count
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var (_, list) in _waiters)
                {
                    total += list.Count;
                }

                return total;
            }
        }
    }

    public static int CountForMemory(object memory)
    {
        lock (_gate)
        {
            var total = 0;
            foreach (var (_, list) in _waiters)
            {
                foreach (var waiter in list)
                {
                    total += ReferenceEquals(waiter.Memory, memory) ? 1 : 0;
                }
            }

            return total;
        }
    }

    public static void Register(ulong address, WaitingDcb waiter)
    {
        waiter.WaitAddress = address;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                list = new List<WaitingDcb>();
                _waiters.Add(address, list);
            }

            list.Add(waiter);
        }
    }

    /// <summary>
    /// Re-evaluates every registered waiter. <paramref name="readValue"/>
    /// receives (address, is64Bit) and returns null when the memory is
    /// unreadable; such waiters are kept registered. Returns the waiters whose
    /// condition is now satisfied (removed from the registry), or null.
    /// </summary>
    public static List<WaitingDcb>? CollectSatisfied(
        object memory,
        Func<ulong, bool, ulong?> readValue,
        Func<WaitingDcb, ulong, bool>? retireUnsatisfied = null)
    {
        List<WaitingDcb>? woken = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(list[i].Memory, memory))
                    {
                        continue;
                    }

                    var value = readValue(address, list[i].Is64Bit);
                    if (value is null ||
                        (!Compare(list[i], value.Value) &&
                         !(retireUnsatisfied?.Invoke(list[i], value.Value) ?? false)))
                    {
                        continue;
                    }

                    woken ??= new List<WaitingDcb>();
                    woken.Add(list[i]);
                    list.RemoveAt(i);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied is not null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return woken;
    }

    /// <summary>
    /// Returns waiters that have remained unsatisfied longer than
    /// <paramref name="maxAgeTicks"/> exactly once, without removing them or
    /// changing their labels. Missing GPU work must fail closed: advancing a
    /// command buffer without its real producer corrupts cross-queue ordering.
    /// </summary>
    public static List<WaitingDcb>? CollectUnreportedStale(
        object memory,
        long nowTicks,
        long maxAgeTicks)
    {
        List<WaitingDcb>? stale = null;
        lock (_gate)
        {
            foreach (var (_, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var waiter = list[i];
                    if (!ReferenceEquals(waiter.Memory, memory) ||
                        waiter.StaleReported ||
                        nowTicks - waiter.RegisteredTicks < maxAgeTicks)
                    {
                        continue;
                    }

                    stale ??= new List<WaitingDcb>();
                    waiter.StaleReported = true;
                    list[i] = waiter;
                    stale.Add(waiter);
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Returns the distinct watched (address, byte-length) ranges for every
    /// waiter still registered against <paramref name="memory"/>. The length is
    /// 8 when any waiter at that address is 64-bit, otherwise 4. Used by the
    /// wait monitor to periodically re-run the cross-queue producer release for
    /// all outstanding waits so a producer that was deferred onto a queue's
    /// pile after that wait's one-shot registration-time sweep still flows.
    /// </summary>
    public static List<(ulong Address, ulong Length)>? SnapshotWaitRanges(object memory)
    {
        List<(ulong Address, ulong Length)>? ranges = null;
        lock (_gate)
        {
            foreach (var (address, list) in _waiters)
            {
                var matched = false;
                var any64Bit = false;
                foreach (var waiter in list)
                {
                    if (!ReferenceEquals(waiter.Memory, memory))
                    {
                        continue;
                    }

                    matched = true;
                    any64Bit |= waiter.Is64Bit;
                }

                if (matched)
                {
                    ranges ??= new List<(ulong, ulong)>();
                    ranges.Add((address, any64Bit ? (ulong)sizeof(ulong) : sizeof(uint)));
                }
            }
        }

        return ranges;
    }

    /// <summary>
    /// Returns watched labels overlapped by a newly discovered producer. Used
    /// only for diagnostics; producer completion still wakes through the
    /// normal CollectSatisfied path after the ordered memory write executes.
    /// </summary>
    public static List<(ulong Address, int Count)> SnapshotInRange(
        object memory,
        ulong start,
        ulong length)
    {
        var matches = new List<(ulong Address, int Count)>();
        if (length == 0)
        {
            return matches;
        }

        var end = start > ulong.MaxValue - length ? ulong.MaxValue : start + length;
        lock (_gate)
        {
            foreach (var (address, list) in _waiters)
            {
                var matchingCount = 0;
                var any64Bit = false;
                foreach (var waiter in list)
                {
                    if (!ReferenceEquals(waiter.Memory, memory))
                    {
                        continue;
                    }

                    matchingCount++;
                    any64Bit |= waiter.Is64Bit;
                }

                if (matchingCount == 0)
                {
                    continue;
                }

                var width = any64Bit
                    ? sizeof(ulong)
                    : sizeof(uint);
                var waitEnd = address > ulong.MaxValue - (ulong)width
                    ? ulong.MaxValue
                    : address + (ulong)width;
                if (start < waitEnd && address < end)
                {
                    matches.Add((address, matchingCount));
                }
            }
        }

        return matches;
    }

    public static bool Compare(in WaitingDcb waiter, ulong value)
    {
        var masked = value & waiter.Mask;
        var reference = waiter.ReferenceValue & waiter.Mask;
        return waiter.CompareFunction switch
        {
            0 => true,
            1 => masked < reference,
            2 => masked <= reference,
            3 => masked == reference,
            4 => masked != reference,
            5 => masked >= reference,
            6 => masked > reference,
            // 7 is reserved; treating it as satisfied keeps a malformed packet
            // from suspending forever.
            _ => true,
        };
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _waiters.Clear();
        }
    }
}
