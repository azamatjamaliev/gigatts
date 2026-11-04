#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

XCODE_DEV="/Applications/Xcode.app/Contents/Developer"
if [[ -d "$XCODE_DEV/Toolchains/XcodeDefault.xctoolchain" ]]; then
  export DEVELOPER_DIR="$XCODE_DEV"
fi

swift build -c release
BIN_DIR="$(swift build -c release --show-bin-path)"
APP="$ROOT/dist/GigaTTS.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/GigaTTS" "$APP/Contents/MacOS/GigaTTS"
cp "$ROOT/App/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/Worker/worker.py" "$APP/Contents/Resources/worker.py"
cp "$ROOT/Worker/requirements.txt" "$APP/Contents/Resources/requirements.txt"
chmod +x "$APP/Contents/MacOS/GigaTTS"
codesign --force --sign - "$APP"

echo "$APP"
