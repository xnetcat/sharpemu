#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

MODE="${1:-run}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_ROOT="${SHARPEMU_GAME_ROOT:-/Users/xnetcat/Downloads/[DLPSGAME.COM]-PPSA06084-app/PPSA06084-app0}"
EBOOT="${SHARPEMU_EBOOT:-$GAME_ROOT/decrypted/eboot.bin}"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
PUBLISH_DIR="$REPO_ROOT/artifacts/publish/SharpEmu.CLI/Release/net10.0/osx-x64"
EMU="$PUBLISH_DIR/SharpEmu"

export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

die() {
  echo "error: $*" >&2
  exit 1
}

case "$MODE" in
  run|--debug|--logs|--telemetry|--verify) ;;
  *) die "unsupported mode '$MODE' (use run, --debug, --logs, --telemetry, or --verify)" ;;
esac

[[ -x "$DOTNET_ROOT/dotnet" ]] || die ".NET SDK is missing from $DOTNET_ROOT"
"$DOTNET_ROOT/dotnet" --list-sdks | grep -q '^10\.0\.103 ' || die ".NET SDK 10.0.103 is required"
arch -x86_64 /usr/bin/true >/dev/null 2>&1 || die "Rosetta 2 is required"
[[ -f "$EBOOT" ]] || die "decrypted executable not found: $EBOOT"
[[ -f "$GAME_ROOT/Media/Metadata/global-metadata.dat" ]] || die "game metadata not found under: $GAME_ROOT"

if [[ -z "${SHARPEMU_FFMPEG_PATH:-}" ]]; then
  SHARPEMU_FFMPEG_PATH="$(command -v ffmpeg || true)"
fi
[[ -n "$SHARPEMU_FFMPEG_PATH" && -x "$SHARPEMU_FFMPEG_PATH" ]] || \
  die "FFmpeg is required for game video playback (install it with: brew install ffmpeg)"
[[ -x "$(dirname "$SHARPEMU_FFMPEG_PATH")/ffprobe" ]] || \
  die "ffprobe is required next to FFmpeg"
export SHARPEMU_FFMPEG_PATH

echo ">> Restoring locked osx-x64 dependencies..."
for project in SharpEmu.Logging SharpEmu.HLE SharpEmu.Core SharpEmu.Libs; do
  dotnet restore "$REPO_ROOT/src/$project/$project.csproj" \
    -r osx-x64 --locked-mode -p:RestoreRecursive=false
done
dotnet restore "$REPO_ROOT/src/SharpEmu.CLI/SharpEmu.CLI.csproj" \
  --locked-mode --no-dependencies --force

echo ">> Publishing SharpEmu for macOS x86_64..."
dotnet publish "$REPO_ROOT/src/SharpEmu.CLI/SharpEmu.CLI.csproj" \
  -c Release -r osx-x64 --self-contained true --no-restore

GLFW_DYLIB="$REPO_ROOT/.packages/ultz.native.glfw/3.4.0/runtimes/osx-x64/native/libglfw.3.dylib"
[[ -f "$GLFW_DYLIB" ]] || die "restored x86_64 GLFW library not found: $GLFW_DYLIB"
cp "$GLFW_DYLIB" "$PUBLISH_DIR/libglfw.3.dylib"

if [[ ! -f "$PUBLISH_DIR/libvulkan.1.dylib" ]] || \
   ! file "$PUBLISH_DIR/libvulkan.1.dylib" | grep -q x86_64; then
  "$REPO_ROOT/scripts/fetch-macos-moltenvk.sh" "$PUBLISH_DIR"
fi

export DYLD_LIBRARY_PATH="$PUBLISH_DIR${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
export SHARPEMU_APP0_DIR="$GAME_ROOT"
export SHARPEMU_STALL_WATCHDOG_SECONDS="${SHARPEMU_STALL_WATCHDOG_SECONDS:-0}"
export SHARPEMU_DISABLE_IMPORT_LOOP_GUARD="${SHARPEMU_DISABLE_IMPORT_LOOP_GUARD:-1}"
export SHARPEMU_PENDING_GUEST_WORK_MB="${SHARPEMU_PENDING_GUEST_WORK_MB:-64}"
export SHARPEMU_RENDER_WORK_BUDGET_MS="${SHARPEMU_RENDER_WORK_BUDGET_MS:-0}"

if [[ "$MODE" == "--telemetry" ]]; then
  export SHARPEMU_LOG_OPEN=1
  export SHARPEMU_LOG_DISASM=1
fi

echo ">> Launching Superliminal from $EBOOT"

if [[ "$MODE" == "--debug" ]]; then
  exec lldb --arch x86_64 -- "$EMU" --cpu-engine=native --log-level=debug "$EBOOT"
fi

if [[ "$MODE" == "--verify" ]]; then
  LOG_DIR="$REPO_ROOT/artifacts/diagnostics/superliminal"
  LOG_FILE="$LOG_DIR/run.log"
  mkdir -p "$LOG_DIR"
  : >"$LOG_FILE"

  SHARPEMU_TRACE_GUEST_IMAGES=present \
  arch -x86_64 "$EMU" --cpu-engine=native --log-level=info "$EBOOT" \
    >"$LOG_FILE" 2>&1 &
  pid=$!

  cleanup() {
    if kill -0 "$pid" 2>/dev/null; then
      kill -TERM "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  }
  trap cleanup EXIT INT TERM

  verify_timeout="${SHARPEMU_VERIFY_TIMEOUT_SECONDS:-180}"
  stability_seconds="${SHARPEMU_VERIFY_STABILITY_SECONDS:-30}"
  [[ "$verify_timeout" =~ ^[1-9][0-9]*$ ]] || \
    die "SHARPEMU_VERIFY_TIMEOUT_SECONDS must be a positive integer"
  [[ "$stability_seconds" =~ ^[1-9][0-9]*$ ]] || \
    die "SHARPEMU_VERIFY_STABILITY_SECONDS must be a positive integer"
  verified_at=0
  deadline=$((SECONDS + verify_timeout))
  while (( SECONDS < deadline )); do
    if grep -Eq 'vk\.guest_image .*nonzero_bytes=[1-9][0-9]*/' "$LOG_FILE" &&
       grep -q 'Vulkan VideoOut presented guest frame' "$LOG_FILE"; then
      if (( verified_at == 0 )); then
        verified_at=$SECONDS
        echo ">> Superliminal presented a non-black guest frame; checking ${stability_seconds}s stability..."
      elif (( SECONDS - verified_at >= stability_seconds )); then
        echo ">> Verified: Superliminal presented a non-black guest frame and remained alive for ${stability_seconds}s."
        exit 0
      fi
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      wait "$pid" || true
      echo "error: SharpEmu exited before Superliminal presented a non-black guest frame" >&2
      tail -n 80 "$LOG_FILE" >&2
      exit 1
    fi
    sleep 1
  done

  if (( verified_at > 0 )); then
    echo "error: Superliminal presented a non-black frame but did not complete the ${stability_seconds}s stability window before the ${verify_timeout}s timeout" >&2
  elif grep -q 'Vulkan VideoOut presented guest frame' "$LOG_FILE"; then
    echo "error: Superliminal only presented an all-black guest frame before the ${verify_timeout}s timeout" >&2
  else
    echo "error: Superliminal did not present a guest frame before the ${verify_timeout}s timeout" >&2
  fi
  tail -n 80 "$LOG_FILE" >&2
  exit 1
fi

exec arch -x86_64 "$EMU" --cpu-engine=native --log-level=info "$EBOOT"
