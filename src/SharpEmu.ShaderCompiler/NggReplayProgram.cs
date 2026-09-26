// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// The vertex program that replays an emulated NGG geometry stage. It exports, from registers
// the replay prologue fills out of one exported vertex record, the same position, auxiliary
// position and parameter outputs the geometry program exported, with the same enable masks.
public static class NggReplayProgram
{
    private const uint Position0Target = 12;
    private const uint Position1Target = 13;
    private const uint FirstParamTarget = 32;
    private const uint LastParamTarget = 63;
    private const uint EndProgramWord = 0xBF810000;

    // The parameter exports the geometry program writes, as a count from parameter 0.
    public static uint ParamCount(Gen5ShaderProgram program)
    {
        var count = 0u;
        foreach (var export in program.Instructions.Select(static instruction => instruction.Control).OfType<Gen5ExportControl>())
        {
            if (export.Target is >= FirstParamTarget and <= LastParamTarget)
            {
                count = Math.Max(count, export.Target - FirstParamTarget + 1);
            }
        }

        return count;
    }

    public static Gen5ShaderProgram Build(Gen5ShaderProgram geometry, uint paramCount)
    {
        var masks = new Dictionary<uint, uint>();
        foreach (var export in geometry.Instructions.Select(static instruction => instruction.Control).OfType<Gen5ExportControl>())
        {
            if (export.Target == Position0Target || export.Target == Position1Target ||
                export.Target is >= FirstParamTarget and <= LastParamTarget && export.Target - FirstParamTarget < paramCount)
            {
                masks[export.Target] = (masks.TryGetValue(export.Target, out var mask) ? mask : 0u) | export.EnableMask;
            }
        }

        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void AddExport(uint target, uint firstRegister)
        {
            if (!masks.TryGetValue(target, out var mask) || mask == 0)
            {
                return;
            }

            var sources = Enumerable.Range(0, 4).Select(component => Gen5Operand.Vector(firstRegister + (uint)component)).ToArray();
            uint[] words =
            [
                0xF8000000u | (target << 4) | mask,
                firstRegister | ((firstRegister + 1) << 8) | ((firstRegister + 2) << 16) | ((firstRegister + 3) << 24),
            ];
            instructions.Add(new Gen5ShaderInstruction(
                pc, Gen5ShaderEncoding.Exp, "Exp", words, sources, [],
                new Gen5ExportControl(target, mask, Compressed: false, Done: false, ValidMask: false)));
            pc += 8;
        }

        // Record dwords 4..7 hold position 0, 8..11 position 1 and 12.. the parameters; the
        // replay prologue loads them from dword 4 on into v0 on.
        AddExport(Position0Target, 0);
        AddExport(Position1Target, 4);
        for (var param = 0u; param < paramCount; param++)
        {
            AddExport(FirstParamTarget + param, 8 + 4 * param);
        }

        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SEndpgm", [EndProgramWord], [], [], null));
        return new Gen5ShaderProgram(geometry.Address, instructions);
    }
}
