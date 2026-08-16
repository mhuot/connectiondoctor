#!/bin/bash
# Packages TBDoctor.app as <name>.dmg (drag to Applications) and <name>.zip.
# Run after build_app.sh (and after notarization + stapling in CI, so the DMG
# contents carry the ticket). SIGN_IDENTITY, if set, also signs the DMG.
# Usage: scripts/make-dmg.sh TBDoctor-1.2.3
set -euo pipefail
cd "$(dirname "$0")/.."
NAME="${1:?usage: make-dmg.sh <artifact-name-without-extension>}"
APP="TBDoctor.app"
[ -d "$APP" ] || { echo "no $APP; run ./build_app.sh first" >&2; exit 1; }

STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$NAME.dmg" "$NAME.zip"
hdiutil create -volname "TBDoctor" -srcfolder "$STAGE" -ov -format UDZO "$NAME.dmg" >/dev/null
rm -rf "$STAGE"
if [ -n "${SIGN_IDENTITY:-}" ]; then
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$NAME.dmg"
fi
ditto -c -k --keepParent "$APP" "$NAME.zip"
ls -la "$NAME.dmg" "$NAME.zip"
