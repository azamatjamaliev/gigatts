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

BUNDLED_MODEL="$ROOT/BundledModels/pyannote-speaker-diarization-community-1"
MODEL_DIR="$ROOT/dist/models/pyannote-speaker-diarization-community-1"
CACHE_MODEL="${HOME}/.cache/gigatts/pyannote-speaker-diarization-community-1"
if [[ -f "$BUNDLED_MODEL/config.yaml" ]]; then
  export GIGATTS_DIARIZATION_MODEL="$BUNDLED_MODEL"
  echo "Использую модель разделения спикеров: $BUNDLED_MODEL"
elif [[ -f "$MODEL_DIR/config.yaml" ]]; then
  export GIGATTS_DIARIZATION_MODEL="$MODEL_DIR"
  echo "Использую модель разделения спикеров: $MODEL_DIR"
elif [[ -f "$CACHE_MODEL/config.yaml" ]]; then
  export GIGATTS_DIARIZATION_MODEL="$CACHE_MODEL"
  echo "Использую модель разделения спикеров: $CACHE_MODEL"
else
  echo "Скачиваю модель разделения спикеров (pyannote)…"
  if ! "$ROOT/Worker/.venv/bin/python" -u "$ROOT/Worker/worker.py" --download-diarization; then
    echo "Разделение спикеров отложено: в сборке нет models/, либо задайте HF_TOKEN (или ~/.cache/gigatts/hf-token)." >&2
    echo "Диктовка и две дорожки звонка работают без этого. Повтор: Worker/.venv/bin/python -u Worker/worker.py --download-diarization" >&2
  fi
fi

echo "Воркер готов. Модель v3_e2e_rnnt скачается при первом запуске GigaПисец."
