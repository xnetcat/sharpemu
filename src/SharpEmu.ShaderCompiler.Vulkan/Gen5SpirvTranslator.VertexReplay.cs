// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private uint _vertexReplayInput;
        private uint _vertexReplayIndex;
        private readonly Dictionary<(uint Parameter, uint Vertex), uint> _vertexReplayOutputs = [];

        private void DeclareVertexReplay()
        {
            if (_request.VertexReplayParameters.Count == 0) return;
            // Replaying a store/atomic would duplicate its effects. Fixed-function
            // vertex inputs also cannot be fetched for another index in this shader.
            if (_request.VertexInputs.Count != 0 || _request.Memory.Entries.Any(memory =>
                    !memory.PlanningOnly && memory.Access != MemoryAccess.Read))
                throw new NotSupportedException("Per-vertex replay requires a read-only shader with shader-based vertex fetches.");
            if (_subgroupInvocationIdInput != 0)
                throw new NotSupportedException("Per-vertex replay cannot duplicate cross-invocation subgroup operations.");
            _vertexReplayInput = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Input, _uvec4Type), SpirvStorageClass.Input);
            _module.AddDecoration(_vertexReplayInput, SpirvDecoration.Location, 0);
            _interfaces.Add(_vertexReplayInput);
            _vertexReplayIndex = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Private, _uintType), SpirvStorageClass.Private);
            _interfaces.Add(_vertexReplayIndex);
            foreach (var parameter in _request.VertexReplayParameters)
            {
                for (uint vertex = 0; vertex < 3; vertex++)
                {
                    var output = _module.AddGlobalVariable(
                        _module.TypePointer(SpirvStorageClass.Output, _vec4Type), SpirvStorageClass.Output);
                    _module.AddDecoration(output, SpirvDecoration.Location, parameter.Location + vertex);
                    _module.AddDecoration(output, SpirvDecoration.Flat);
                    _interfaces.Add(output);
                    _vertexReplayOutputs.Add((parameter.Parameter, vertex), output);
                }
            }
        }

        private uint EmitVertexReplayEntry(uint body, uint functionType)
        {
            var outputs = new List<(uint Variable, uint Type)> { (_positionOutput, _vec4Type) };
            outputs.AddRange(_vertexOutputs.Values.Select(variable => (variable, _vec4Type)));
            if (_pointSizeOutput != 0) outputs.Add((_pointSizeOutput, _floatType));
            if (_layerOutput != 0) outputs.Add((_layerOutput, _uintType));
            if (_viewportIndexOutput != 0) outputs.Add((_viewportIndexOutput, _uintType));
            if (_clipDistanceOutput != 0) outputs.Add((_clipDistanceOutput, _module.TypeArray(_floatType, _clipDistanceCount)));
            if (_cullDistanceOutput != 0) outputs.Add((_cullDistanceOutput, _module.TypeArray(_floatType, _cullDistanceCount)));
            var saved = outputs.Select(output => _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Private, output.Type), SpirvStorageClass.Private)).ToArray();
            _interfaces.AddRange(saved);
            var entry = _module.BeginFunction(_voidType, functionType);
            _module.AddName(entry, "replay_triangle_vertices");
            _module.AddLabel();
            var indices = Load(_uvec4Type, _vertexReplayInput);
            var corner = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, indices, 3);
            for (uint vertex = 0; vertex < 3; vertex++)
            {
                // Each replay starts with the same register state as a fresh
                // invocation; masked/partial writes must not inherit a prior corner.
                foreach (var register in _scalarRegisterVariables.Values) Store(register, UInt(0));
                foreach (var register in _vectorRegisterVariables.Values) Store(register, UInt(0));
                foreach (var register in _laneSpillSlots.Values) Store(register, UInt(0));
                if (_packedHalfRegisters != 0)
                    Store(_packedHalfRegisters, _module.ConstantNull(_module.TypeArray(_vec2Type, VectorRegisterCount)));
                Store(_vertexReplayIndex, _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, indices, vertex));
                if (_iterationGuard != 0) Store(_iterationGuard, UInt(0));
                _module.AddInstruction(SpirvOp.FunctionCall, _voidType, body);
                foreach (var parameter in _request.VertexReplayParameters)
                    Store(_vertexReplayOutputs[(parameter.Parameter, vertex)],
                        _vertexOutputs.TryGetValue(parameter.Parameter, out var output)
                            ? Load(_vec4Type, output) : _module.ConstantNull(_vec4Type));
                var selected = _module.AddInstruction(SpirvOp.IEqual, _boolType, corner, UInt(vertex));
                var save = _module.AllocateId();
                var merge = _module.AllocateId();
                _module.AddStatement(SpirvOp.SelectionMerge, merge, 0);
                _module.AddStatement(SpirvOp.BranchConditional, selected, save, merge);
                _module.AddLabel(save);
                for (var i = 0; i < outputs.Count; i++)
                    Store(saved[i], Load(outputs[i].Type, outputs[i].Variable));
                _module.AddStatement(SpirvOp.Branch, merge);
                _module.AddLabel(merge);
            }
            for (var i = 0; i < outputs.Count; i++)
                Store(outputs[i].Variable, Load(outputs[i].Type, saved[i]));
            _module.AddStatement(SpirvOp.Return);
            _module.EndFunction();
            return entry;
        }
    }
}
