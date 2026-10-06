<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Rendering resolution

Options → Rendering exposes two independent sizes, globally and per game:

- **Resolution** sets the host window size or exclusive fullscreen mode.
- **Game display mode** reports a 1080p display to the game by default. Select
  3840×2160 to request a 4K display. A registered presentation buffer cannot
  change this setting.

The CLI equivalents are `--resolution=1280x720` and
`--guest-resolution=1920x1080` (or `3840x2160`). For example, a 1080p game display
can be presented in a 720p window; changing the window alone does not reduce the
work the game renders.

The game chooses its internal render targets. Some games adapt those targets to
the reported display mode; others render at a fixed resolution and may still
register 4K buffers with a 1080p display. Silent Hill: The Short Message does so
in current testing. The size of that presentation buffer alone also does not
establish the sizes of its intermediate scene targets.

Arbitrary internal resolution scaling is not implemented. The previous
`RenderResolutionScale` setting and its `SHARPEMU_RENDER_SCALE` export had no
renderer consumer; they have been removed. They did not change rendering work.
A correct implementation must preserve guest memory layouts while scaling host
attachments, viewports, scissors, image fetches, compute accesses, aliases and
readbacks consistently. Reducing only the output image or window is not an
internal-resolution implementation.
