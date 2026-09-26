// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// Emulated NGG geometry stage. The merged export and geometry program runs as one 64-lane
// compute workgroup per subgroup: its system registers come from an input record the host
// builds, and every export is stored to that lane's record instead of a stage output. A
// replay vertex program then reads the exported primitives and vertices back for rasterization.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private uint _nggRecordAddress;

        private uint LogicalOr(uint left, uint right) => _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);

        private uint IMul(uint left, uint right) => _module.AddInstruction(SpirvOp.IMul, _uintType, left, right);

        private uint NggRecordDwords => NggRecordLayout.RecordDwords(_request.NggParamCount);

        // Buffer 0 is the input records, buffer 1 the exported records.
        private uint NggBufferAddress(uint buffer)
        {
            var first = _request.Bindings.NggBuffersDword + buffer * 2;
            var low = Widen(LoadShaderDataDword(UInt(first)));
            var high = Widen(LoadShaderDataDword(UInt(first + 1)));
            return _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                low,
                _module.AddInstruction(SpirvOp.ShiftLeftLogical, _ulongType, high, ULong(32)));
        }

        private uint NggDwordAddress(uint base64, uint dwordIndex) =>
            IAdd64(base64, _module.AddInstruction(SpirvOp.IMul, _ulongType, Widen(dwordIndex), ULong(sizeof(uint))));

        private uint LoadNggDword(uint address64) =>
            _module.AddInstruction(SpirvOp.Load, _uintType, DeviceWordPointer(address64), 2u, 4u);

        private void StoreNggDword(uint address64, uint value) =>
            _module.AddStatement(SpirvOp.Store, DeviceWordPointer(address64), value, 2u, 4u);

        // The subgroup's system registers and this lane's input VGPRs, and a null primitive in
        // this lane's record until the program exports one.
        private void EmitNggComputeInitialState(uint lane, uint group)
        {
            var privateUlongPointer = _module.TypePointer(SpirvStorageClass.Private, _ulongType);
            _nggRecordAddress = _module.AddGlobalVariable(privateUlongPointer, SpirvStorageClass.Private, ULong(0));
            _module.AddName(_nggRecordAddress, "nggRecordAddress");
            _interfaces.Add(_nggRecordAddress);

            var groupBase = NggDwordAddress(NggBufferAddress(0), IMul(group, UInt(NggRecordLayout.InputGroupDwords)));
            StoreS(2, LoadNggDword(NggDwordAddress(groupBase, UInt(0))));
            StoreS(3, LoadNggDword(NggDwordAddress(groupBase, UInt(1))));
            var laneBase = IAdd(UInt(NggRecordLayout.InputHeaderDwords), IMul(lane, UInt(NggRecordLayout.InputLaneDwords)));
            for (uint register = 0; register < NggRecordLayout.InputLaneDwords; register++)
            {
                StoreV(register, LoadNggDword(NggDwordAddress(groupBase, IAdd(laneBase, UInt(register)))), guardWithExec: false);
            }

            var slot = IAdd(IMul(group, UInt(NggRecordLayout.WaveLanes)), lane);
            var record = NggDwordAddress(NggBufferAddress(1), IMul(slot, UInt(NggRecordDwords)));
            Store(_nggRecordAddress, record);
            StoreNggDword(NggDwordAddress(record, UInt(NggRecordLayout.PrimitiveDword)), UInt(0x8000_0000u));
        }

        // Diagnostic: every thread stores its debug slots to its own record of the output
        // buffer, in workgroup-major thread order.
        private void EmitComputeDebugInitialState(uint localId, uint workGroupId)
        {
            var privateUlongPointer = _module.TypePointer(SpirvStorageClass.Private, _ulongType);
            _nggRecordAddress = _module.AddGlobalVariable(privateUlongPointer, SpirvStorageClass.Private, ULong(0));
            _module.AddName(_nggRecordAddress, "nggRecordAddress");
            _interfaces.Add(_nggRecordAddress);
            uint Component(uint vector, uint index) => _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, vector, index);
            var local = IAdd(
                Component(localId, 0),
                IMul(IAdd(Component(localId, 1), IMul(Component(localId, 2), UInt(_localSizeY))), UInt(_localSizeX)));
            var groupSize = _localSizeX * _localSizeY * _localSizeZ;
            var thread = IAdd(IMul(Component(workGroupId, 0), UInt(groupSize)), local);
            Store(_nggRecordAddress, NggDwordAddress(NggBufferAddress(1), IMul(thread, UInt(NggRecordDwords))));
        }

        private static readonly string[] NggDebugRegisters =
            (Environment.GetEnvironmentVariable("SHARPEMU_NGG_DEBUG_REGS") ?? "s0,s2,s3,s4,s106,s107,v0,v6,v8,v9,v10,v18,v19,v21,v22")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        

        // Diagnostic: exec, then a fixed set of scalar and vector registers after a listed pc.
        private void EmitNggDebugCapture(uint pc)
        {
            var slot = Array.IndexOf(NggRecordLayout.DebugPcs, pc);
            if (slot < 0)
            {
                return;
            }

            var record = Load(_ulongType, _nggRecordAddress);
            var first = NggRecordLayout.FirstParamDword + 4 * _request.NggParamCount + (uint)slot * NggRecordLayout.DebugSlotDwords;
            var values = new List<uint>
            {
                BitwiseOr(
                    _module.AddInstruction(SpirvOp.Select, _uintType, Load(_boolType, _exec), UInt(1), UInt(0)),
                    _module.AddInstruction(SpirvOp.Select, _uintType, Load(_boolType, _scc), UInt(2), UInt(0))),
            };
            foreach (var register in NggDebugRegisters)
            {
                var index = uint.Parse(register[1..], System.Globalization.CultureInfo.InvariantCulture);
                values.Add(register[0] switch
                {
                    's' => LoadS(index),
                    // The dword length the shader sees for a buffer binding, and its byte bias.
                    'L' => _module.AddInstruction(
                        SpirvOp.ArrayLength, _uintType,
                        _module.AddInstruction(SpirvOp.AccessChain, _storageBlockPointer, _globalBuffers, UInt(index)), 0),
                    'B' => Load(_uintType, RuntimeBufferBiasPointer((int)index)),
                    _ => LoadV(index),
                });
            }
            for (var index = 0; index < values.Count && index < NggRecordLayout.DebugSlotDwords; index++)
            {
                StoreNggDword(NggDwordAddress(record, UInt(first + (uint)index)), values[index]);
            }
        }

        // Stores the enabled components of one export to this lane's record.
        private bool EmitNggExportStore(Gen5ShaderInstruction instruction, Gen5ExportControl export)
        {
            uint field;
            if (export.Target == 20)
            {
                field = NggRecordLayout.PrimitiveDword;
            }
            else if (export.Target == 12)
            {
                field = NggRecordLayout.Position0Dword;
            }
            else if (export.Target == 13)
            {
                field = NggRecordLayout.Position1Dword;
            }
            else if (export.Target is >= 32 and < 64 && export.Target - 32 < _request.NggParamCount)
            {
                field = NggRecordLayout.FirstParamDword + 4 * (export.Target - 32);
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    continue;
                }

                components[component] = export.Compressed
                    ? Bitcast(_uintType, LoadCompressedExportComponent(instruction, component))
                    : LoadV(instruction.Sources[component].Value);
            }

            var record = Load(_ulongType, _nggRecordAddress);
            EmitConditional(Load(_boolType, _exec), () =>
            {
                for (var component = 0; component < 4; component++)
                {
                    if ((export.EnableMask & (1u << component)) != 0)
                    {
                        StoreNggDword(NggDwordAddress(record, UInt(field + (uint)component)), components[component]);
                    }
                }
            });
            return true;
        }

        // Vertex v of the replay draw is corner v % 3 of exported primitive v / 3. A primitive
        // packs its three vertex slots in 10-bit fields; a null primitive collapses to one point.
        private void EmitNggReplayInitialState(uint vertexIndex)
        {
            var records = NggBufferAddress(1);
            var recordDwords = UInt(NggRecordDwords);
            var primitiveIndex = _module.AddInstruction(SpirvOp.UDiv, _uintType, vertexIndex, UInt(3));
            var corner = _module.AddInstruction(SpirvOp.UMod, _uintType, vertexIndex, UInt(3));
            var primitive = LoadNggDword(NggDwordAddress(records, IMul(primitiveIndex, recordDwords)));
            var isNull = _module.AddInstruction(
                SpirvOp.INotEqual, _boolType, BitwiseAnd(primitive, UInt(0x8000_0000u)), UInt(0));
            var vertexSlot = BitwiseAnd(ShiftRightLogical(primitive, IMul(corner, UInt(10))), UInt(0x1FF));
            // A slot outside the subgroup cannot name an exported vertex; treat it as null too.
            isNull = LogicalOr(isNull, _module.AddInstruction(
                SpirvOp.UGreaterThanEqual, _boolType, vertexSlot, UInt(NggRecordLayout.WaveLanes)));
            vertexSlot = _module.AddInstruction(SpirvOp.Select, _uintType, isNull, UInt(0), vertexSlot);
            var groupFirstSlot = BitwiseAnd(primitiveIndex, UInt(~(NggRecordLayout.WaveLanes - 1)));
            var record = NggDwordAddress(records, IMul(IAdd(groupFirstSlot, vertexSlot), recordDwords));
            var loadedDwords = NggRecordDwords - NggRecordLayout.Position0Dword;
            for (uint register = 0; register < loadedDwords; register++)
            {
                StoreV(register, LoadNggDword(NggDwordAddress(record, UInt(NggRecordLayout.Position0Dword + register))), guardWithExec: false);
            }
        }
    }
}
