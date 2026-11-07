#!/usr/bin/env python3
"""Local GigaAM worker. JSON lines on stdin, one JSON object per line on stdout.

Models come from https://github.com/salute-developers/GigaAM (MIT, Salute Developers).
Library logs are sent to stderr so they cannot break the protocol.
"""

from __future__ import annotations

import json
import math
import os
import shutil
import struct
import sys
import tempfile
import traceback
import wave

REAL_STDOUT = sys.stdout
sys.stdout = sys.stderr

SAMPLE_RATE = 16000
MAX_CHUNK_SECONDS = 20.0
FRAME_SECONDS = 0.02


def reply(payload: dict) -> None:
    REAL_STDOUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    REAL_STDOUT.flush()


def require_ffmpeg() -> None:
    if shutil.which("ffmpeg") is None:
        raise RuntimeError(
            "ffmpeg не найден. Нажмите «Установить распознавание» в GigaTTS."
        )


def load_model(model_name: str):
    require_ffmpeg()
    import torch
    import gigaam

    threads = min(4, os.cpu_count() or 4)
    torch.set_num_threads(threads)
    os.environ.setdefault("OMP_NUM_THREADS", str(threads))

    device = os.environ.get("GIGATTS_DEVICE", "cpu").strip() or "cpu"
    if device == "mps" and not torch.backends.mps.is_available():
        print("MPS недоступен, используется CPU", file=sys.stderr, flush=True)
        device = "cpu"
    elif device == "cuda" and not torch.cuda.is_available():
        print("CUDA недоступна, используется CPU", file=sys.stderr, flush=True)
        device = "cpu"
    elif device not in ("cpu", "mps", "cuda"):
        print(f"Неизвестное устройство {device}, используется CPU", file=sys.stderr, flush=True)
        device = "cpu"

    def _load(target: str):
        return gigaam.load_model(
            model_name,
            device=target,
            fp16_encoder=False,
            use_flash=False,
        )

    try:
        model = _load(device)
    except Exception:
        if device == "cpu":
            raise
        print(f"Не удалось загрузить модель на {device}, повтор на CPU", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        device = "cpu"
        model = _load(device)
    print(f"model ready: {model_name} on {device}", file=sys.stderr, flush=True)
    return model


def read_wav(path: str) -> bytes:
    with wave.open(path, "rb") as handle:
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        rate = handle.getframerate()
        frames = handle.readframes(handle.getnframes())
    if width != 2:
        raise RuntimeError(f"Ожидался 16-bit PCM, получен sampwidth={width}")
    if channels == 2:
        count = len(frames) // 4
        samples = struct.unpack("<" + "h" * (count * 2), frames[: count * 4])
        mono = bytearray()
        for index in range(count):
            left = samples[index * 2]
            right = samples[index * 2 + 1]
            mixed = int((left + right) / 2)
            mono += struct.pack("<h", mixed)
        frames = bytes(mono)
    elif channels != 1:
        raise RuntimeError(f"Неподдерживаемое число каналов: {channels}")
    if rate != SAMPLE_RATE:
        frames = resample_pcm16(frames, rate, SAMPLE_RATE)
    return frames


def resample_pcm16(pcm: bytes, source_rate: int, target_rate: int) -> bytes:
    count = len(pcm) // 2
    if count == 0 or source_rate == target_rate:
        return pcm
    samples = struct.unpack("<" + "h" * count, pcm[: count * 2])
    ratio = source_rate / target_rate
    out_count = int(count / ratio)
    out = []
    for index in range(out_count):
        position = index * ratio
        left = int(position)
        fraction = position - left
        right = min(left + 1, count - 1)
        value = samples[left] * (1 - fraction) + samples[right] * fraction
        out.append(int(max(-32768, min(32767, value))))
    return struct.pack("<" + "h" * len(out), *out) if out else b""


def write_wav(path: str, pcm: bytes) -> None:
    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes(pcm)


def frame_rms(pcm: bytes) -> int:
    count = len(pcm) // 2
    if count == 0:
        return 0
    samples = struct.unpack("<" + "h" * count, pcm[: count * 2])
    total = 0
    for sample in samples:
        total += sample * sample
    return int(math.sqrt(total / count))


def speech_spans(pcm: bytes) -> list[tuple[float, float]]:
    frame_bytes = int(SAMPLE_RATE * FRAME_SECONDS) * 2
    if frame_bytes <= 0 or len(pcm) < frame_bytes:
        duration = (len(pcm) // 2) / SAMPLE_RATE
        if duration >= 0.25:
            return [(0.0, min(duration, MAX_CHUNK_SECONDS))]
        return []

    energies = []
    for offset in range(0, len(pcm) - frame_bytes + 1, frame_bytes):
        energies.append(frame_rms(pcm[offset : offset + frame_bytes]))
    if not energies:
        return []

    ordered = sorted(energies)
    noise = ordered[max(0, int(len(ordered) * 0.2) - 1)]
    threshold = max(180, int(noise * 2.8))
    hang_frames = 8
    min_frames = 5

    raw: list[tuple[int, int]] = []
    in_speech = False
    start = 0
    silence = 0
    for index, energy in enumerate(energies):
        if energy >= threshold:
            if not in_speech:
                in_speech = True
                start = index
            silence = 0
        elif in_speech:
            silence += 1
            if silence >= hang_frames:
                end = index - silence + 1
                if end - start >= min_frames:
                    raw.append((start, end))
                in_speech = False
    if in_speech and len(energies) - start >= min_frames:
        raw.append((start, len(energies)))

    merged: list[tuple[int, int]] = []
    gap_frames = int(0.35 / FRAME_SECONDS)
    for start, end in raw:
        if merged and start - merged[-1][1] <= gap_frames:
            merged[-1] = (merged[-1][0], end)
        else:
            merged.append((start, end))

    duration = (len(pcm) // 2) / SAMPLE_RATE
    if not merged and duration >= 0.25:
        merged = [(0, len(energies))]

    pad_frames = int(0.12 / FRAME_SECONDS)
    max_frames = int(MAX_CHUNK_SECONDS / FRAME_SECONDS)
    spans: list[tuple[float, float]] = []
    last_frame = len(energies)
    for start, end in merged:
        start = max(0, start - pad_frames)
        end = min(last_frame, end + pad_frames)
        cursor = start
        while end - cursor > max_frames:
            spans.append((cursor * FRAME_SECONDS, (cursor + max_frames) * FRAME_SECONDS))
            cursor += max_frames
        if end > cursor:
            spans.append((cursor * FRAME_SECONDS, min(duration, end * FRAME_SECONDS)))
    return spans


def transcribe_pcm(model, pcm: bytes, origin: float) -> list[dict]:
    spans = speech_spans(pcm)
    segments = []
    for start, end in spans:
        first = int(start * SAMPLE_RATE) * 2
        last = int(end * SAMPLE_RATE) * 2
        chunk = pcm[first:last]
        if len(chunk) < int(SAMPLE_RATE * 0.2) * 2:
            continue
        descriptor, path = tempfile.mkstemp(suffix=".wav", prefix="gigatts-")
        os.close(descriptor)
        try:
            write_wav(path, chunk)
            result = model.transcribe(path)
            text = getattr(result, "text", str(result)).strip()
        finally:
            try:
                os.remove(path)
            except OSError:
                pass
        if text:
            segments.append(
                {
                    "start": round(origin + start, 3),
                    "end": round(origin + end, 3),
                    "text": text,
                }
            )
    return segments


def transcribe_file(model, path: str) -> str:
    if not path or not os.path.exists(path):
        raise RuntimeError("Аудиофайл диктовки не найден")
    pcm = read_wav(path)
    if len(pcm) < int(SAMPLE_RATE * 0.2) * 2:
        return ""
    parts = [segment["text"] for segment in transcribe_pcm(model, pcm, 0.0)]
    return " ".join(parts).strip()


def transcribe_call(model, mic_path: str | None, system_path: str | None) -> list[dict]:
    collected = []
    if mic_path and os.path.exists(mic_path) and os.path.getsize(mic_path) > 44:
        for segment in transcribe_pcm(model, read_wav(mic_path), 0.0):
            segment["speaker"] = "me"
            collected.append(segment)
    if system_path and os.path.exists(system_path) and os.path.getsize(system_path) > 44:
        for segment in transcribe_pcm(model, read_wav(system_path), 0.0):
            segment["speaker"] = "them"
            collected.append(segment)
    collected.sort(key=lambda item: (item["start"], 0 if item["speaker"] == "me" else 1))
    return collected


def handle(model, request: dict):
    command = request.get("cmd")
    if command == "load":
        loaded = load_model(request.get("model") or "v3_e2e_rnnt")
        return loaded, {"ready": True}
    if model is None:
        raise RuntimeError("Модель ещё не загружена")
    if command == "transcribe":
        return model, {"text": transcribe_file(model, request.get("path") or "")}
    if command == "transcribe_call":
        return model, {
            "segments": transcribe_call(model, request.get("mic"), request.get("system"))
        }
    if command == "shutdown":
        return model, {}
    raise RuntimeError(f"Неизвестная команда: {command}")


def main() -> int:
    model = None
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request_id = ""
        try:
            request = json.loads(line)
            request_id = str(request.get("id") or "")
            model, extra = handle(model, request)
            payload = {"id": request_id, "ok": True}
            payload.update(extra)
            reply(payload)
            if request.get("cmd") == "shutdown":
                return 0
        except Exception as exc:  # noqa: BLE001 - protocol boundary
            traceback.print_exc(file=sys.stderr)
            reply({"id": request_id, "ok": False, "error": str(exc)})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
