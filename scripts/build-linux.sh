#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
case "$RID" in
  linux-x64|linux-arm64) ;;
  *)
    echo "Укажите linux-x64 или linux-arm64." >&2
    exit 1
    ;;
esac

if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  export PATH="${HOME}/.dotnet:${PATH}"
  export DOTNET_ROOT="${HOME}/.dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Нужен .NET SDK 8 или новее: https://dotnet.microsoft.com/download" >&2
  exit 1
fi

if [[ "$RID" == "linux-x64" ]]; then
  NAME="GigaTTS-linux"
  ZIP="$ROOT/dist/GigaTTS-linux-x64.zip"
else
  NAME="GigaTTS-linux-arm64"
  ZIP="$ROOT/dist/GigaTTS-linux-arm64.zip"
fi
OUT="$ROOT/dist/$NAME"

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
cp "$ROOT/NOTICE" "$OUT/NOTICE"
"$ROOT/scripts/bundle-diarization-model.sh" \
  "$OUT/models/pyannote-speaker-diarization-community-1"
cp "$ROOT/Windows/src/Assets/icon.png" "$OUT/icon.png"
cat > "$OUT/GigaTTS.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Version=1.0
Name=GigaПисец
Comment=Локальная диктовка и расшифровка звонков
Exec=GigaTTS
Icon=icon.png
Terminal=false
Categories=Audio;Utility;
StartupNotify=false
EOF
chmod +x "$OUT/GigaTTS"

echo "$OUT"
if command -v zip >/dev/null 2>&1; then
  rm -f "$ZIP"
  (cd "$ROOT/dist" && zip -r -q "$(basename "$ZIP")" "$(basename "$OUT")")
  echo "$ZIP"
fi
