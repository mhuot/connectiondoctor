#!/bin/bash
# Builds the Connection Dashboard and stages it for embedding in TBDoctor.
#
# The dashboard is the shared UI for TBDoctor (macOS) and ConnectionDoctor
# (Windows); it lives in its own repo and is consumed here as a build artifact.
# The staged output is git-ignored on purpose — build output does not belong in
# this repo's history, and a stale committed copy would be worse than none.
#
# Node is needed to run this script. It is NOT needed to run TBDoctor: the
# bundle is compiled into the binary, so users never see npm.
#
# Usage: scripts/build-ui.sh [path-to-connection-dashboard]
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
DASHBOARD="${1:-$ROOT/../connection-dashboard}"
APP="$DASHBOARD/app"

if [ ! -d "$APP" ]; then
  echo "No dashboard checkout at '$APP'." >&2
  echo "Clone mhuot/connection-dashboard beside this repo, or pass its path." >&2
  exit 1
fi

if ! command -v npm >/dev/null 2>&1; then
  echo "npm is not on PATH. Node is required to build the dashboard bundle" >&2
  echo "(but not to run TBDoctor)." >&2
  exit 1
fi

pushd "$APP" >/dev/null
[ -d node_modules ] || { echo "Installing dashboard dependencies…"; npm install --no-fund --no-audit; }
echo "Building dashboard…"
npm run build
popd >/dev/null

[ -f "$APP/dist/index.html" ] || { echo "Dashboard build produced no index.html." >&2; exit 1; }

TARGET="$ROOT/Sources/TBDoctor/ui"
# Keep .gitkeep: it is what makes SwiftPM's .copy("ui") resolve in a clean checkout.
find "$TARGET" -mindepth 1 ! -name '.gitkeep' -delete
cp -R "$APP/dist/." "$TARGET/"

COUNT=$(find "$TARGET" -type f ! -name '.gitkeep' | wc -l | tr -d ' ')
echo "Staged $COUNT files into $TARGET"
echo "Rebuild TBDoctor to embed them:  ./build_app.sh"
