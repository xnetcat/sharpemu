// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Holds DCBs whose parsing was suspended on an unsatisfied WAIT_REG_MEM
/// condition. AgcExports re-checks every waiter against guest memory on each
/// submit and resumes the ones whose condition became true (labels are advanced
/// by ReleaseMem / WriteData / DmaData packets, or by direct CPU writes).
///
/// This preserves cross-submit ordering: the work that follows a wait inside a
/// DCB is only queued once the awaited completion label is genuinely written,
/// instead of being force-satisfied at parse time and running ahead of the
/// compute/graphics work it depends on (which produced a black composite).
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
        public bool Is64Bit;
        // Stopwatch timestamp captured at registration; used only by the
        // stale-waiter safety valve so a label that is never written cannot
        // suspend its DCB forever.
        public long RegisteredTicks;
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
    public static List<WaitingDcb>? CollectSatisfied(Func<ulong, bool, ulong?> readValue)
    {
        List<WaitingDcb>? woken = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var value = readValue(address, list[i].Is64Bit);
                    if (value is null || !Compare(list[i], value.Value))
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
    /// Safety valve: removes and returns waiters that have been suspended for
    /// longer than <paramref name="maxAgeTicks"/> so a completion label that is
    /// never written (e.g. one the guest expects from work we do not model)
    /// cannot deadlock its DCB. The caller force-satisfies + resumes them.
    /// </summary>
    public static List<WaitingDcb>? CollectStale(long nowTicks, long maxAgeTicks)
    {
        List<WaitingDcb>? stale = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (nowTicks - list[i].RegisteredTicks < maxAgeTicks)
                    {
                        continue;
                    }

                    stale ??= new List<WaitingDcb>();
                    stale.Add(list[i]);
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

        return stale;
    }

    public static bool Compare(in WaitingDcb waiter, ulong value)
    {
        var masked = value & waiter.Mask;
        var reference = waiter.ReferenceValue & waiter.Mask;
        return waiter.CompareFunction switch
        {
            1 => masked < reference,
            2 => masked <= reference,
            3 => masked == reference,
            4 => masked != reference,
            5 => masked >= reference,
            6 => masked > reference,
            // 0 is "always" in the PM4 encoding and 7 is reserved; treating both
            // as satisfied keeps a malformed packet from suspending forever.
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
