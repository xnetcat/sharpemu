// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Ampr;

public static partial class AmprExports
{
    internal enum AprStep { Ready, Waiting, Completed }
    private readonly record struct AprCommand(ulong Offset, byte Kind, ulong A, ulong B, ulong C = 0, ulong D = 0);

    internal sealed class AprBatch
    {
        private readonly CpuContext _context;
        private readonly ulong _commandBuffer;
        private readonly AprCommand[] _commands;
        private int _index;
        internal int ExecutionResult { get; private set; }
        internal uint ErrorOffset { get; private set; }
        internal Action? BeforeFinalSignal { get; set; }

        private AprBatch(CpuContext context, ulong commandBuffer, AprCommand[] commands)
        {
            // Do not retain mutable guest registers on the asynchronous worker.
            _context = new CpuContext(context.Memory, context.TargetGeneration);
            _commandBuffer = commandBuffer;
            _commands = commands;
        }

        internal AprStep Step(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_index >= _commands.Length) return AprStep.Completed;
            var command = _commands[_index];
            var result = 0;
            // A terminal marker/event lets a polling guest reuse its result
            // storage immediately. Publish success before making it observable.
            if (_index == _commands.Length - 1 && command.Kind is 1 or 2)
                BeforeFinalSignal?.Invoke();
            switch (command.Kind)
            {
                case 0:
                    result = CompleteReadFileRecord(_context, _commandBuffer, new ReadFileCommand
                    {
                        RecordOffset = command.Offset, FileId = (uint)command.A,
                        Destination = command.B, Size = command.C, FileOffset = command.D,
                    }, cancellation);
                    break;
                case 1:
                    if (!KernelEventQueueCompatExports.TriggerAmprEvent(command.A, (uint)command.B, command.C))
                        result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                    break;
                case 2:
                    if (!_context.TryWriteUInt64(command.A, command.B))
                        result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    break;
                case 3:
                    if (command.D != 0) Thread.MemoryBarrier();
                    if (!_context.TryReadUInt64(command.A, out var value))
                        result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    else if (!IsWaitConditionSatisfied(value, command.B, (uint)command.C))
                        return AprStep.Waiting;
                    break;
            }
            if (result != 0)
            {
                ExecutionResult = result;
                ErrorOffset = (uint)command.Offset;
                return AprStep.Completed;
            }
            return ++_index == _commands.Length ? AprStep.Completed : AprStep.Ready;
        }

        internal static int Capture(CpuContext context, ulong commandBuffer, out AprBatch? batch)
        {
            batch = null;
            if (commandBuffer == 0) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            if (!TryGetCommandBufferState(context, commandBuffer, out _, out _, out var state) || state is null)
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            AprCommand[] commands;
            lock (state)
            {
                commands = new AprCommand[state.ReadFileCommands.Count + state.KernelEventCommands.Count +
                    state.WriteAddressCommands.Count + state.WaitAddressCommands.Count];
                var index = 0;
                foreach (var command in state.ReadFileCommands)
                    commands[index++] = new(command.RecordOffset, 0, command.FileId, command.Destination, command.Size, command.FileOffset);
                foreach (var command in state.KernelEventCommands)
                    commands[index++] = new(command.RecordOffset, 1, command.Equeue, unchecked((uint)command.Id), command.Data);
                foreach (var command in state.WriteAddressCommands)
                    commands[index++] = new(command.RecordOffset, 2, command.Address, command.Value);
                foreach (var command in state.WaitAddressCommands)
                    commands[index++] = new(command.RecordOffset, 3, command.Address, command.Reference, command.Compare, command.Flush);
            }
            Array.Sort(commands, static (left, right) => left.Offset.CompareTo(right.Offset));
            batch = new(context, commandBuffer, commands);
            return 0;
        }
    }

    internal static int CaptureCommandBuffer(CpuContext context, ulong commandBuffer, out AprBatch? batch) =>
        AprBatch.Capture(context, commandBuffer, out batch);
}
