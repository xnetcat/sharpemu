// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    // A waterfall loop visits the first remaining lane, executes the lanes with
    // that same key, removes them, and restores EXEC before the next iteration.
    // Arbitrary V_READLANE instructions must not acquire first-active-lane semantics.
    private static bool IsRemainingLaneSelector(IReadOnlyList<Gen5ShaderInstruction> code, int index)
    {
        if (index < 2 || index + 3 >= code.Count) return false;
        var read = code[index];
        var scan = code[index - 1];
        var initial = code[index - 2];
        if (read.Opcode != "VReadlaneB32" || read.Sources.Count < 2 ||
            scan is not { Opcode: "SFF1I32B64", Sources.Count: 1, Destinations.Count: 1 } ||
            scan.Destinations[0] != read.Sources[1] ||
            initial is not { Opcode: "SMovB64", Sources.Count: 1, Destinations.Count: 1 } ||
            initial.Destinations[0] != scan.Sources[0] || initial.Sources[0] != Gen5Operand.Scalar(126))
            return false;
        var remaining = scan.Sources[0];
        var equal = code[index + 1];
        var save = code[index + 2];
        var skip = code[index + 3];
        if (equal is not { Opcode: "VCmpEqU32", Sources.Count: 2, Destinations.Count: 1 } ||
            !equal.Sources.Contains(read.Destinations[0]) || !equal.Sources.Contains(read.Sources[0]) ||
            save is not { Opcode: "SAndSaveexecB64", Sources.Count: 1, Destinations.Count: 1 } ||
            save.Sources[0] != equal.Destinations[0] || skip.Opcode != "SCbranchExecz") return false;
        var tailPc = Target(skip);
        var tail = FindInstructionIndex(code, tailPc);
        if (tail <= index + 3 || tail + 2 >= code.Count) return false;
        var clear = code[tail];
        var restore = code[tail + 1];
        var repeat = code[tail + 2];
        if (clear is not { Opcode: "SAndn2B64", Sources.Count: 2, Destinations.Count: 1 } ||
            clear.Destinations[0] != remaining || clear.Sources[0] != remaining || clear.Sources[1] != equal.Destinations[0] ||
            restore is not { Opcode: "SMovB64", Sources.Count: 1, Destinations.Count: 1 } ||
            restore.Destinations[0] != Gen5Operand.Scalar(126) || restore.Sources[0] != save.Destinations[0] ||
            repeat.Opcode != "SCbranchScc1" || Target(repeat) != scan.Pc) return false;
        // No body write may replace the remaining mask or its saved EXEC value.
        for (var i = index + 4; i < tail; i++)
            if (WritesMask(code[i], remaining) || WritesMask(code[i], save.Destinations[0]) ||
                WritesMask(code[i], equal.Destinations[0]) || WritesMask(code[i], Gen5Operand.Scalar(126)) ||
                WritesLaneRegister(code[i], read.Sources[0]))
                return false;
        return true;

        static uint Target(Gen5ShaderInstruction branch) =>
            unchecked((uint)(branch.Pc + 4 + (short)(branch.Words[0] & 0xFFFF) * 4));
    }

    private static bool WritesMask(Gen5ShaderInstruction instruction, Gen5Operand low) =>
        WritesLaneRegister(instruction, low) ||
        WritesLaneRegister(instruction, Gen5Operand.Scalar(low.Value + 1));

    private static bool WritesLaneRegister(Gen5ShaderInstruction instruction, Gen5Operand register)
    {
        if (register.Kind == Gen5OperandKind.ScalarRegister)
        {
            if (register.Value is 126 or 127 &&
                (instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                 instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal) ||
                 instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal))) return true;
            var scalarDestination = instruction.Control switch
            {
                Gen5Vop3Control control => control.ScalarDestination,
                Gen5SdwaControl control => control.ScalarDestination,
                _ => null,
            };
            if (scalarDestination is { } scalar && register.Value >= scalar && register.Value - scalar < 2)
                return true;
        }
        // Memory instructions enumerate every destination dword in the IR.
        // Scalar ALU pairs may instead name only the low register.
        var width = instruction.Destinations.Count == 1 &&
            instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
        return instruction.Destinations.Any(destination => destination.Kind == register.Kind &&
            register.Value >= destination.Value && register.Value - destination.Value < width);
    }

    private static bool HasMaskBitClear(IReadOnlyList<Gen5ShaderInstruction> code, Gen5Operand mask, Gen5Operand bit)
    {
        for (var i = 0; i < code.Count; i++)
        {
            var clear = code[i];
            if (clear.Opcode == "SBitset0B32" && clear.Destinations.Contains(mask) && clear.Sources.Contains(bit)) return true;
            if (clear is not { Opcode: "SXorB32", Sources.Count: 2, Destinations.Count: 1 } ||
                clear.Destinations[0] != mask || !clear.Sources.Contains(mask)) continue;
            var shifted = clear.Sources[0] == mask ? clear.Sources[1] : clear.Sources[0];
            var definition = FindLastDefinition(code, i, shifted);
            if (definition >= 0 && code[definition] is { Opcode: "SLshlB32", Sources.Count: 2 } shift &&
                shift.Sources[1] == bit && TryGetConstant(shift.Sources[0], out var one) && one == 1)
                return true;
        }
        return false;
    }
}
