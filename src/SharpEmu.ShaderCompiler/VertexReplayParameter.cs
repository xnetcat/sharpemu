// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// Three flat vec4 locations carry the exact exported words of a triangle's vertices.
public readonly record struct VertexReplayParameter(uint Parameter, uint Location);
