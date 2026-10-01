// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class DeviceAddressPreparation
{
    // A finite guessed cap is not a proof. Only skip the global memory sweep
    // when every device access has an exact host-evaluable range. Indirect
    // descriptors, BVH traversal and unanalysed continuations keep the fallback.
    internal static int BoundedRangeCount(Gen5ShaderProgram program, ShaderResourcePlan plan)
    {
        if (program.Instructions.Any(instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64")) return -1;
        var covered = new HashSet<int>();
        foreach (var range in plan.DeviceAddressRanges)
        {
            if (!range.Bounded || !range.Plannable) return -1;
            foreach (var index in range.MemoryIndices) covered.Add(index);
        }
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay") return -1;
            if (memory.PlanningOnly)
            {
                // Scalar descriptor reads suppressed by the indirect-image
                // planner emit no device load, even without a flattened slot.
                if (memory.Kind is not (MemoryResourceKind.ScalarAddress or MemoryResourceKind.ScalarBuffer) &&
                    !plan.FlattenedSlotByMemoryIndex.ContainsKey(index)) return -1;
                continue;
            }
            if (memory.DeviceDescriptor) return -1;
            if (memory.Kind is MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer)
            {
                if (memory.BufferDescriptor is not { Provenance: BufferDescriptorProvenance.Static }) return -1;
            }
            else if (memory.Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Global ||
                memory.Kind == MemoryResourceKind.Flat && memory.AddressSpace == FlatAddressSpace.Global)
            {
                if (!covered.Contains(index)) return -1;
            }
        }
        return plan.DeviceAddressRanges.Count;
    }

    internal static bool CanUseBoundedPreparation(ShaderProgramInfo program, ResourceSnapshot snapshot)
    {
        if (!program.UsesDeviceAddresses) return true;
        if (program.BoundedDeviceAddressRangeCount < 0 ||
            program.BoundedDeviceAddressRangeCount != snapshot.DeviceAddressRanges.Length) return false;
        foreach (var range in snapshot.DeviceAddressRanges)
            if (!range.Planned) return false;
        return true;
    }
}
