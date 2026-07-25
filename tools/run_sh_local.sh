#!/bin/zsh
# Silent Hill (PPSA10112) launcher for the silent-hill-work branch build in
# /Users/xnetcat/Projects/xnetcat/sharpemu. Derived from
# /Volumes/Untitled/sharpemu/tools/run_sh.sh and encodes the same ops hazards:
#  - NEVER osascript-focus the PPSA10112 window (spurious window-close deaths).
#  - Kill/inspect by PID, never by bare "SharpEmu" process name.
#  - Full traces are disk-lethal; bound trace runs with `head -c`.
#
# Usage: run_sh_local.sh <logfile> <statusfile> [RUN_SECONDS]
set -u
WT="/Users/xnetcat/Projects/xnetcat/sharpemu"
PUB="$WT/artifacts/publish/SharpEmu.CLI/Release/net10.0/osx-x64"
GAME_ROOT="/Volumes/Untitled/sharpemu/games/PPSA10112/PPSA10112-app"
EBOOT="$GAME_ROOT/eboot.bin"
LOG="${1:?logfile required}"
STATUS="${2:?statusfile required}"
RUN_SECONDS="${3:-360}"

if [[ ! -x "$PUB/SharpEmu" ]]; then
  echo "FATAL: $PUB/SharpEmu not found; publish first." > "$STATUS"
  exit 1
fi

# dotnet publish does not stage the GLFW native for osx-x64; without it there is
# no window at all (the run proceeds headless and looks like a hang).
if [[ ! -f "$PUB/libglfw.3.dylib" ]]; then
  GLFW="$WT/.packages/ultz.native.glfw/3.4.0/runtimes/osx-x64/native/libglfw.3.dylib"
  if [[ -f "$GLFW" ]]; then
    cp "$GLFW" "$PUB/" && echo "staged libglfw.3.dylib" >> "$STATUS"
  else
    echo "FATAL: libglfw.3.dylib missing at $GLFW — no window would open." > "$STATUS"
    exit 1
  fi
fi
if [[ ! -f "$PUB/libvulkan.1.dylib" ]]; then
  echo "FATAL: MoltenVK missing — video output silently disabled. Run scripts/fetch-macos-moltenvk.sh" > "$STATUS"
  exit 1
fi

ulimit -c unlimited
export DYLD_LIBRARY_PATH="$PUB"
export SHARPEMU_APP0_DIR="$GAME_ROOT"

export SHARPEMU_PERIODIC_SNAPSHOT_SECONDS="${SHARPEMU_PERIODIC_SNAPSHOT_SECONDS:-30}"
export SHARPEMU_STALL_WATCHDOG_SECONDS="${SHARPEMU_STALL_WATCHDOG_SECONDS:-20}"

if [[ -z "${SHARPEMU_AUTO_CROSS:-}" ]]; then
  export SHARPEMU_AUTO_CROSS="$(seq 30 6 $((RUN_SECONDS > 30 ? RUN_SECONDS - 10 : 30)) | paste -sd, -)"
fi

echo "START $(date '+%H:%M:%S') run_seconds=$RUN_SECONDS branch=$(git -C "$WT" branch --show-current) head=$(git -C "$WT" rev-parse --short HEAD)" > "$STATUS"
echo "AUTO_CROSS=$SHARPEMU_AUTO_CROSS" >> "$STATUS"

"$PUB/SharpEmu" --cpu-engine=native "$EBOOT" > "$LOG" 2>&1 &
EMU_PID=$!
echo "PID=$EMU_PID" >> "$STATUS"

SECS=0
while [[ $SECS -lt $RUN_SECONDS ]]; do
  if ! kill -0 "$EMU_PID" 2>/dev/null; then
    echo "DIED_EARLY after ${SECS}s" >> "$STATUS"
    break
  fi
  sleep 5
  SECS=$((SECS + 5))
done

if kill -0 "$EMU_PID" 2>/dev/null; then
  echo "STOPPING at ${SECS}s (still alive)" >> "$STATUS"
  kill -TERM "$EMU_PID" 2>/dev/null
  sleep 3
  kill -9 "$EMU_PID" 2>/dev/null
  echo "ALIVE_AT_END=1" >> "$STATUS"
else
  echo "ALIVE_AT_END=0" >> "$STATUS"
fi
wait "$EMU_PID" 2>/dev/null
RC=$?
echo "EXIT_CODE=$RC" >> "$STATUS"
echo "ENDED $(date '+%H:%M:%S') log_bytes=$(stat -f%z "$LOG" 2>/dev/null)" >> "$STATUS"
