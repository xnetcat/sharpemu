// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Both allocation and submission visibility must agree for direct host reads.
internal static class MappedBufferReadback
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_MAPPED_BUFFER_READBACK") == "1";
}
