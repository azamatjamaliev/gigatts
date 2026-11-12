#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

XCODE_DEV="/Applications/Xcode.app/Contents/Developer"
if [[ -d "$XCODE_DEV/Toolchains/XcodeDefault.xctoolchain" ]]; then
  export DEVELOPER_DIR="$XCODE_DEV"
fi

# Display / Finder name for the installable artifact.
# Internal binary stays GigaTTS (CFBundleExecutable); Application Support stays GigaTTS.
APP_NAME="GigaПисец"
APP="$ROOT/dist/${APP_NAME}.app"
DMG="$ROOT/dist/${APP_NAME}.dmg"
LEGACY_APP="$ROOT/dist/GigaTTS.app"

swift build -c release
BIN_DIR="$(swift build -c release --show-bin-path)"

rm -rf "$APP" "$LEGACY_APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/GigaTTS" "$APP/Contents/MacOS/GigaTTS"
cp "$ROOT/App/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/App/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cp "$ROOT/Worker/worker.py" "$APP/Contents/Resources/worker.py"
cp "$ROOT/Worker/requirements.txt" "$APP/Contents/Resources/requirements.txt"
cp "$ROOT/NOTICE" "$APP/Contents/Resources/NOTICE"
"$ROOT/scripts/bundle-diarization-model.sh" \
  "$APP/Contents/Resources/models/pyannote-speaker-diarization-community-1"
chmod +x "$APP/Contents/MacOS/GigaTTS"
codesign --force --sign - "$APP"

# Simple drag-to-Applications DMG
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/gigapisets-dmg.XXXXXX")"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT
cp -R "$APP" "$STAGE/${APP_NAME}.app"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$STAGE" \
  -ov \
  -format UDZO \
  "$DMG" >/dev/null

echo "$APP"
echo "$DMG"
