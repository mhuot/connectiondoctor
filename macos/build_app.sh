#!/bin/bash
# Assembles TBDoctor.app from the SwiftPM binary.
# SwiftPM produces a bare executable; a menu bar app needs a bundle so macOS
# gives it an Info.plist, a bundle identifier, and LSUIElement (no Dock icon).
set -euo pipefail

cd "$(dirname "$0")"
CONFIG="${1:-release}"
APP="TBDoctor.app"
# VERSION comes from the release tag in CI (docs/distribution.md); local builds
# default to a dev marker. SIGN_IDENTITY selects a Developer ID certificate;
# empty means ad-hoc, which is enough to run locally.
VERSION="${VERSION:-0.0.0-dev}"
SIGN_IDENTITY="${SIGN_IDENTITY:-}"

echo "Building ($CONFIG)…"
swift build -c "$CONFIG"
BIN="$(swift build -c "$CONFIG" --show-bin-path)/TBDoctor"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/TBDoctor"

# SwiftPM emits target resources as a side-by-side bundle. Bundle.module looks
# in Bundle.main.resourceURL first, so the dashboard only reaches the .app if
# that bundle is copied into Contents/Resources.
RESOURCES="$(swift build -c "$CONFIG" --show-bin-path)/TBDoctor_TBDoctor.bundle"
if [ -d "$RESOURCES" ]; then
  cp -R "$RESOURCES" "$APP/Contents/Resources/"
  if [ -f "$RESOURCES/ui/index.html" ]; then
    echo "Embedded the Connection Dashboard bundle."
  else
    echo "note: no dashboard staged; run ../scripts/build-ui.sh macos for the built-in UI"
  fi
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>TBDoctor</string>
    <key>CFBundleDisplayName</key>     <string>TBDoctor</string>
    <key>CFBundleExecutable</key>      <string>TBDoctor</string>
    <key>CFBundleIdentifier</key>      <string>net.mhuot.tbdoctor</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>${VERSION%%-*}</string>
    <key>CFBundleVersion</key>         <string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key>  <string>14.0</string>
    <!-- Menu bar only: no Dock icon, no window on launch. -->
    <key>LSUIElement</key>             <true/>
    <key>NSHumanReadableCopyright</key><string></string>
</dict>
</plist>
PLIST

if [ -n "$SIGN_IDENTITY" ]; then
  # Developer ID + hardened runtime + timestamp: what notarization requires.
  codesign --force --deep --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP"
  echo "Signed with: $SIGN_IDENTITY"
else
  # Ad-hoc signature. Enough to run locally; CI passes SIGN_IDENTITY for releases.
  codesign --force --deep --sign - "$APP" 2>/dev/null || echo "note: codesign skipped"
fi

echo "Built $APP"
echo
echo "Run it:            open $APP"
echo "Start at login:    System Settings → General → Login Items → +"
echo "CLI:               $APP/Contents/MacOS/TBDoctor --report"
echo "Register with Claude Code:"
echo "  claude mcp add tbdoctor -- $PWD/$APP/Contents/MacOS/TBDoctor --mcp"
