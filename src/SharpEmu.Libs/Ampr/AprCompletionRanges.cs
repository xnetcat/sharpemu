// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Ampr;

// Completed IDs need no context, event, or command snapshot. Adjacent completions
// with the same result share a range, including when the caller polls its own
// completion address and does not call the kernel wait export immediately.
// The owner serializes access. Taking an ID consumes it exactly once.
internal sealed class AprCompletionRanges
{
    private readonly List<Range> _ranges = [];
    private readonly record struct Range(uint First, uint Last, int Result);
    internal int RangeCount => _ranges.Count;
    internal void Clear() => _ranges.Clear();
    private int LowerBound(uint id)
    {
        var low = 0; var high = _ranges.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_ranges[middle].Last < id) low = middle + 1; else high = middle;
        }
        return low;
    }
    internal bool Contains(uint id)
    {
        var index = LowerBound(id);
        return index < _ranges.Count && _ranges[index].First <= id;
    }
    internal void Add(uint id, int result)
    {
        var index = LowerBound(id);
        var first = id; var last = id;
        if (index < _ranges.Count && _ranges[index].First <= id)
            throw new InvalidOperationException("APR ID completed twice.");
        if (index > 0 && (ulong)_ranges[index - 1].Last + 1 == id && _ranges[index - 1].Result == result)
        {
            first = _ranges[index - 1].First;
            _ranges.RemoveAt(--index);
        }
        if (index < _ranges.Count && (ulong)last + 1 == _ranges[index].First && _ranges[index].Result == result)
        {
            last = _ranges[index].Last;
            _ranges.RemoveAt(index);
        }
        _ranges.Insert(index, new(first, last, result));
    }
    internal bool TryTake(uint id, out int result)
    {
        result = 0;
        var index = LowerBound(id);
        if (index == _ranges.Count || _ranges[index].First > id) return false;
        var range = _ranges[index];
        result = range.Result;
        _ranges.RemoveAt(index);
        if (id < range.Last) _ranges.Insert(index, range with { First = id + 1 });
        if (id > range.First) _ranges.Insert(index, range with { Last = id - 1 });
        return true;
    }
}
