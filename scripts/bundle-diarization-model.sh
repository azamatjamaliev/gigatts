#!/bin/bash
# Copy essential pyannote community-1 files into a packaging destination.
# Skips README/gif/HF cache metadata. Prefers committed BundledModels/…,
# then dist/models/… or ~/.cache/gigatts/… as fallbacks.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIRNAME="pyannote-speaker-diarization-community-1"
DEST="${1:-}"
if [[ -z "$DEST" ]]; then
  echo "Usage: $0 <destination-dir>" >&2
  exit 2
fi

is_ready() {
  local root="$1"
  [[ -f "$root/config.yaml" \
    && -f "$root/embedding/pytorch_model.bin" \
    && -f "$root/segmentation/pytorch_model.bin" \
    && -f "$root/plda/plda.npz" \
    && -f "$root/plda/xvec_transform.npz" ]]
}

SOURCE="${GIGATTS_DIARIZATION_SOURCE:-}"
if [[ -z "$SOURCE" ]]; then
  for candidate in \
    "$ROOT/BundledModels/$DIRNAME" \
    "$ROOT/dist/models/$DIRNAME" \
    "${HOME}/.cache/gigatts/$DIRNAME"
  do
    if is_ready "$candidate"; then
      SOURCE="$candidate"
      break
    fi
  done
fi

if [[ -z "$SOURCE" ]] || ! is_ready "$SOURCE"; then
  echo "Нет локальной модели diarization для упаковки." >&2
  echo "Ожидается BundledModels/$DIRNAME в репозитории (после clone)." >&2
  echo "Либо скачайте один раз (нужен HF_TOKEN):" >&2
  echo "  Worker/.venv/bin/python -u Worker/worker.py --download-diarization" >&2
  echo "или положите готовую папку в dist/models/$DIRNAME" >&2
  echo "(config.yaml, embedding/, segmentation/, plda/)." >&2
  exit 1
fi

rm -rf "$DEST"
mkdir -p "$DEST/embedding" "$DEST/segmentation" "$DEST/plda"
cp "$SOURCE/config.yaml" "$DEST/config.yaml"
cp "$SOURCE/embedding/pytorch_model.bin" "$DEST/embedding/pytorch_model.bin"
cp "$SOURCE/segmentation/pytorch_model.bin" "$DEST/segmentation/pytorch_model.bin"
cp "$SOURCE/plda/plda.npz" "$DEST/plda/plda.npz"
cp "$SOURCE/plda/xvec_transform.npz" "$DEST/plda/xvec_transform.npz"

SIZE="$(du -sh "$DEST" | awk '{print $1}')"
echo "diarization bundled: $DEST ($SIZE) ← $SOURCE"
