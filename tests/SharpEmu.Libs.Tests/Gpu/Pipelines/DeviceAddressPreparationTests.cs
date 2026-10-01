// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class DeviceAddressPreparationTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(16u)]
    public void ScalarPayloadAndHostBufferStoreHaveCompleteRanges(uint dwords)
    {
        var program = FlattenedReadReuseTests.RepeatedReadProgram(dwords);
        var plan = Extract(program, userDataCount: 9, flattenStandaloneScalarReads: false);
        Assert.Equal(1, DeviceAddressPreparation.BoundedRangeCount(program, plan));
    }

    [Fact]
    public void DynamicOffsetKeepsGlobalSweepDespitePlannableBase()
    {
        var program = FlattenedReadReuseTests.RepeatedReadProgram(1, dynamicOffset: true);
        var plan = Extract(program, userDataCount: 9, flattenStandaloneScalarReads: false);
        Assert.True(Assert.Single(plan.DeviceAddressRanges).Plannable);
        Assert.Equal(-1, DeviceAddressPreparation.BoundedRangeCount(program, plan));
    }

    [Fact]
    public void DeviceDescriptorAndUnanalysedMemoryKeepGlobalSweep()
    {
        var program = FlattenedReadReuseTests.RepeatedReadProgram(1);
        var plan = Extract(program, userDataCount: 9, flattenStandaloneScalarReads: false);
        var buffer = plan.Memory.Entries.First(memory => memory.Kind == MemoryResourceKind.Buffer);
        buffer.DeviceDescriptor = true;
        Assert.Equal(-1, DeviceAddressPreparation.BoundedRangeCount(program, plan));
        buffer.DeviceDescriptor = false;
        buffer.PlanningOnly = true;
        Assert.Equal(-1, DeviceAddressPreparation.BoundedRangeCount(program, plan));
    }

    [Fact]
    public void MissingOrFailedRuntimeRangeKeepsGlobalSweep()
    {
        var program = new ShaderProgramInfo { UsesDeviceAddresses = true, BoundedDeviceAddressRangeCount = 1 };
        Assert.False(DeviceAddressPreparation.CanUseBoundedPreparation(program, new()));
        var snapshot = new ResourceSnapshot { DeviceAddressRanges = [new(0, 0x1000, 32, false, false)] };
        Assert.False(DeviceAddressPreparation.CanUseBoundedPreparation(program, snapshot));
        snapshot.DeviceAddressRanges[0] = new(0, 0x1000, 32, true, false);
        Assert.True(DeviceAddressPreparation.CanUseBoundedPreparation(program, snapshot));
        Assert.False(DeviceAddressPreparation.CanUseBoundedPreparation(new() { UsesDeviceAddresses = true }, snapshot));
    }
}
