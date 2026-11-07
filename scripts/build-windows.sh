#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-win-x64}"
case "$RID" in
  win-x64|win-arm64) ;;
  *)
    echo "Укажите win-x64 или win-arm64." >&2
    exit 1
    ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Нужен .NET SDK 8 или новее: https://dotnet.microsoft.com/download" >&2
  exit 1
fi

NAME="GigaTTS-windows"
if [[ "$RID" != "win-x64" ]]; then
  NAME="GigaTTS-$RID"
fi
OUT="$ROOT/dist/$NAME"
ZIP="$ROOT/dist/$NAME.zip"
if [[ "$RID" == "win-x64" ]]; then
  ZIP="$ROOT/dist/GigaTTS-windows-x64.zip"
fi

rm -rf "$OUT"
dotnet publish "$ROOT/Windows/src/GigaTTS.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$OUT" \
  -p:DebugType=none \
  -p:DebugSymbols=false
cp "$ROOT/Worker/worker.py" "$OUT/worker.py"
cp "$ROOT/Worker/requirements.txt" "$OUT/requirements.txt"

if command -v zip >/dev/null 2>&1; then
  rm -f "$ZIP"
  (cd "$ROOT/dist" && zip -r -q "$(basename "$ZIP")" "$(basename "$OUT")")
  echo "$ZIP"
else
  echo "$OUT"
fi
