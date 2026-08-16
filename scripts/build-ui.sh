#!/bin/bash
# Builds the Connection Dashboard once and stages it into both collectors:
#   macos/Sources/TBDoctor/ui        (TBDoctor, SwiftPM resource)
#   windows/src/ConnectionDoctor/ui  (ConnectionDoctor, EmbeddedResource)
#
# The staged output is git-ignored on purpose — build output does not belong in
# history, and a stale committed copy would be worse than none. Node is needed
# to run this script; it is NOT needed to run either collector, because the
# bundle is compiled into the binary.
#
# Usage: scripts/build-ui.sh [macos|windows|all]   (default: all)
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
APP="$ROOT/dashboard"
WHICH="${1:-all}"

if ! command -v npm >/dev/null 2>&1; then
  echo "npm is not on PATH. Node is required to build the dashboard bundle" >&2
  echo "(but not to run TBDoctor or ConnectionDoctor)." >&2
  exit 1
fi

pushd "$APP" >/dev/null
[ -d node_modules ] || { echo "Installing dashboard dependencies…"; npm ci --no-fund --no-audit; }
echo "Building dashboard…"
npm run build
popd >/dev/null

[ -f "$APP/dist/index.html" ] || { echo "Dashboard build produced no index.html." >&2; exit 1; }

stage() {
  local target="$1"
  mkdir -p "$target"
  # Keep .gitkeep where present: it is what makes SwiftPM's .copy("ui") resolve in a clean checkout.
  find "$target" -mindepth 1 ! -name '.gitkeep' -delete
  cp -R "$APP/dist/." "$target/"
  local count; count=$(find "$target" -type f ! -name '.gitkeep' | wc -l | tr -d ' ')
  echo "Staged $count files into ${target#$ROOT/}"
}

case "$WHICH" in
  macos)   stage "$ROOT/macos/Sources/TBDoctor/ui" ;;
  windows) stage "$ROOT/windows/src/ConnectionDoctor/ui" ;;
  all)     stage "$ROOT/macos/Sources/TBDoctor/ui"; stage "$ROOT/windows/src/ConnectionDoctor/ui" ;;
  *) echo "usage: scripts/build-ui.sh [macos|windows|all]" >&2; exit 1 ;;
esac

echo "Rebuild the collector(s) to embed it:  macos/build_app.sh  ·  dotnet build windows/ConnectionDoctor.sln"
