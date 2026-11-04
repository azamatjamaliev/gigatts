#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SUPPORT="${HOME}/Library/Application Support/GigaTTS"
mkdir -p "$SUPPORT"

if ! command -v brew >/dev/null 2>&1; then
  echo "Нужен Homebrew, чтобы поставить Python 3.12 и ffmpeg." >&2
  exit 1
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  brew install ffmpeg
fi

PYTHON=""
for candidate in python3.12 python3.13 python3.11 python3.10; do
  if command -v "$candidate" >/dev/null 2>&1; then
    PYTHON="$(command -v "$candidate")"
    break
  fi
done

if [[ -z "$PYTHON" ]]; then
  brew install python@3.12
  PYTHON="$(brew --prefix python@3.12)/bin/python3.12"
fi

echo "Python: $PYTHON"
"$PYTHON" -m venv "$ROOT/Worker/.venv"
"$ROOT/Worker/.venv/bin/python" -m pip install --upgrade pip
"$ROOT/Worker/.venv/bin/pip" install -r "$ROOT/Worker/requirements.txt"

printf '%s\n' "$ROOT/Worker/.venv/bin/python" > "$SUPPORT/python-path"
printf '%s\n' "$ROOT/Worker/worker.py" > "$SUPPORT/worker-path"

echo "Воркер готов. Модель v3_e2e_rnnt скачается при первом запуске GigaTTS."
