// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ampr;

// A fixed worker set bounds host reads and read-ahead memory. A waiting command
// yields its worker; a different buffer can then publish the awaited value.
internal sealed class AprExecutor
{
    internal const int Capacity = 256;
    private readonly object _gate = new();
    private readonly List<Work> _pending = [];
    private readonly Thread[] _workers;
    private readonly CancellationTokenSource _cancellation = new();
    private int _active;
    private long _sequence;
    private bool _stopping;

    internal sealed class Work(AmprExports.AprBatch batch, ulong priority, Action<int, uint> complete)
    {
        internal readonly AmprExports.AprBatch Batch = batch;
        internal readonly ulong Priority = priority;
        internal readonly Action<int, uint> Complete = complete;
        internal long Sequence;
        internal long ReadyAt;
    }

    internal AprExecutor(int workerCount = 4)
    {
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(Run) { IsBackground = true, Name = $"APR I/O {i}" };
            _workers[i].Start();
        }
    }

    internal bool TrySubmit(Work work, Func<bool> publish)
    {
        lock (_gate)
        {
            if (_stopping || _active >= Capacity) return false;
            // Publish ID before any command can complete, but only after a queue
            // slot is reserved. A failed guest write causes no I/O side effects.
            if (!publish()) return false;
            work.Sequence = ++_sequence;
            _active++;
            _pending.Add(work);
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    internal void Stop()
    {
        lock (_gate) { _stopping = true; _cancellation.Cancel(); Monitor.PulseAll(_gate); }
    }

    internal bool Join(TimeSpan timeout)
    {
        var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        foreach (var worker in _workers)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, end - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (!worker.Join(remaining)) return false;
        }
        return true;
    }

    private void Run()
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                while (true)
                {
                    if (_stopping && _pending.Count == 0) return;
                    var now = Stopwatch.GetTimestamp();
                    var selected = -1;
                    for (var i = 0; i < _pending.Count; i++)
                    {
                        var candidate = _pending[i];
                        if (!_stopping && candidate.ReadyAt > now) continue;
                        if (selected < 0 || candidate.Priority < _pending[selected].Priority ||
                            (candidate.Priority == _pending[selected].Priority && candidate.Sequence < _pending[selected].Sequence))
                            selected = i;
                    }
                    if (selected >= 0) { work = _pending[selected]; _pending.RemoveAt(selected); break; }
                    Monitor.Wait(_gate, _pending.Count == 0 ? Timeout.Infinite : 1);
                }
            }
            AmprExports.AprStep step;
            var result = 0;
            uint offset = 0;
            try
            {
                step = work.Batch.Step(_cancellation.Token);
                result = work.Batch.ExecutionResult;
                offset = work.Batch.ErrorOffset;
            }
            catch (OperationCanceledException)
            {
                step = AmprExports.AprStep.Completed;
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
            }
            catch (Exception error)
            {
                step = AmprExports.AprStep.Completed;
                result = unchecked((int)0x80020005);
                Console.Error.WriteLine($"[APR] Command execution failed: {error.GetType().Name}: {error.Message}");
            }
            if (step == AmprExports.AprStep.Completed)
            {
                try { work.Complete(result, offset); }
                finally { lock (_gate) { _active--; Monitor.PulseAll(_gate); } }
            }
            else
            {
                lock (_gate)
                {
                    work.ReadyAt = step == AmprExports.AprStep.Waiting ? Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000 : 0;
                    work.Sequence = ++_sequence;
                    _pending.Add(work);
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
