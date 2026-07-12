# Bink 2 bridge

Demon's Souls plays Bink 2 (.bk2) files through a Bink implementation linked
directly into eboot.bin. It does not use libSceVideodec, therefore an HLE video
decoder cannot observe or replace those frames.

SharpEmu observes successful guest .bk2 opens and, when a Bink bridge is
available, presents its decoded BGRA frames at the normal guest-flip boundary.
This preserves the game's own timing and lets the host Vulkan presenter display
the movie without trying to execute the PS5-specific Bink GPU decode path.

## Supplying the adapter

Bink 2 is proprietary. Obtain a compatible Mac Bink 2 SDK from RAD Game Tools,
then compile sharpemu_bink2_bridge.c against the SDK's bink.h and Mac library.
The adapter deliberately contains only a three-function C ABI so the managed
emulator never depends on RAD's private binary ABI.

Place the resulting libsharpemu_bink2_bridge.dylib next to the SharpEmu
executable, or point to it explicitly:

    SHARPEMU_BINK2_BRIDGE=/absolute/path/libsharpemu_bink2_bridge.dylib \
      ./SharpEmu /path/to/eboot.bin

The expected exports are sharpemu_bink2_open_utf8,
sharpemu_bink2_decode_next_bgra, and sharpemu_bink2_close. The supplied
adapter opens one movie, exposes BGRA pixels, and advances after each decoded
frame. The managed side validates dimensions and retains ownership of the
destination buffer.

If the bridge is absent, SharpEmu logs one informational line and retains the
existing guest rendering path; it does not substitute a random movie or change
game input/state.
