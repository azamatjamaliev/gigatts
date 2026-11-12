#!/usr/bin/env python3
"""Local GigaAM worker. JSON lines on stdin, one JSON object per line on stdout.

Models come from https://github.com/salute-developers/GigaAM (MIT, Salute Developers).
Call diarization uses pyannote/speaker-diarization-community-1 whenever there
are not two meaningful separate tracks (mic+system). Dual-track keeps source
labels (me/them); single-track/mix uses them1/them2 via pyannote. Library logs
go to stderr so they cannot break the protocol.
"""

from __future__ import annotations

# OS trust store, before urllib or gigaam downloads. Verification stays on.
# Python.org on Windows can reject a chain that Schannel already accepts
# (expired Let's Encrypt cross-sign). A wrong clock still fails, on purpose.
try:
    import truststore
    truststore.inject_into_ssl()
except Exception:
    pass

import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import traceback
import wave

REAL_STDOUT = sys.stdout
sys.stdout = sys.stderr

SAMPLE_RATE = 16000
MAX_CHUNK_SECONDS = 20.0
FRAME_SECONDS = 0.02
MIN_SPEECH_SECONDS = 0.3
# Ниже этого RMS кадр/дорожка считаются тишиной (не «речью для dual-track»).
MIN_SPEECH_RMS = 180
DIARIZATION_REPO = "pyannote/speaker-diarization-community-1"
DIARIZATION_DIRNAME = "pyannote-speaker-diarization-community-1"

_diarization_pipeline = None


def reply(payload: dict) -> None:
    REAL_STDOUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    REAL_STDOUT.flush()


def require_ffmpeg() -> None:
    if shutil.which("ffmpeg") is None:
        raise RuntimeError(
            "ffmpeg не найден. Нажмите «Установить распознавание» в GigaПисец."
        )


def resolve_device() -> str:
    device = os.environ.get("GIGATTS_DEVICE", "cpu").strip() or "cpu"
    import torch

    if device == "mps" and not torch.backends.mps.is_available():
        print("MPS недоступен, используется CPU", file=sys.stderr, flush=True)
        return "cpu"
    if device == "cuda" and not torch.cuda.is_available():
        print("CUDA недоступна, используется CPU", file=sys.stderr, flush=True)
        return "cpu"
    if device not in ("cpu", "mps", "cuda"):
        print(f"Неизвестное устройство {device}, используется CPU", file=sys.stderr, flush=True)
        return "cpu"
    return device


def load_model(model_name: str):
    require_ffmpeg()
    import torch
    import gigaam

    threads = min(4, os.cpu_count() or 4)
    torch.set_num_threads(threads)
    os.environ.setdefault("OMP_NUM_THREADS", str(threads))

    device = resolve_device()

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


def cache_root() -> str:
    override = os.environ.get("GIGATTS_CACHE", "").strip()
    if override:
        return override
    return os.path.join(os.path.expanduser("~"), ".cache", "gigatts")


def diarization_required_files(root: str) -> list[str]:
    return [
        os.path.join(root, "config.yaml"),
        os.path.join(root, "embedding", "pytorch_model.bin"),
        os.path.join(root, "segmentation", "pytorch_model.bin"),
        os.path.join(root, "plda", "plda.npz"),
        os.path.join(root, "plda", "xvec_transform.npz"),
    ]


def diarization_ready(path: str | None = None) -> bool:
    root = path or diarization_model_dir()
    return all(os.path.isfile(item) for item in diarization_required_files(root))


def bundled_diarization_candidates() -> list[str]:
    """Folders shipped next to worker.py (app Resources / install dir)."""
    here = os.path.dirname(os.path.abspath(__file__))
    return [os.path.join(here, "models", DIARIZATION_DIRNAME)]


def diarization_cache_dir() -> str:
    return os.path.join(cache_root(), DIARIZATION_DIRNAME)


def diarization_model_dir() -> str:
    """Prefer env override, then bundled weights, then user cache."""
    override = os.environ.get("GIGATTS_DIARIZATION_MODEL", "").strip()
    if override and diarization_ready(override):
        return override
    for candidate in bundled_diarization_candidates():
        if diarization_ready(candidate):
            return candidate
    cached = diarization_cache_dir()
    if diarization_ready(cached):
        return cached
    if override:
        return override
    return cached


def hf_token() -> str | None:
    for key in ("GIGATTS_HF_TOKEN", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"):
        value = os.environ.get(key, "").strip()
        if value:
            return value
    token_path = os.path.join(cache_root(), "hf-token")
    try:
        with open(token_path, encoding="utf-8") as handle:
            value = handle.read().strip()
        if value:
            return value
    except OSError:
        pass
    return None


def download_diarization_model() -> str:
    """Use bundled/cache weights when present; otherwise fetch into the user cache."""
    existing = diarization_model_dir()
    if diarization_ready(existing):
        print(f"diarization already present: {existing}", file=sys.stderr, flush=True)
        return existing

    require_ffmpeg()
    from huggingface_hub import snapshot_download

    # Never write into a read-only app bundle; HF downloads go to the user cache.
    destination = diarization_cache_dir()
    if diarization_ready(destination):
        print(f"diarization already present: {destination}", file=sys.stderr, flush=True)
        return destination

    os.makedirs(os.path.dirname(destination) or ".", exist_ok=True)
    token = hf_token()
    hint = (
        "Примите условия модели на Hugging Face и задайте токен: "
        "переменная HF_TOKEN / GIGATTS_HF_TOKEN или файл ~/.cache/gigatts/hf-token. "
        f"Модель: https://huggingface.co/{DIARIZATION_REPO}. "
        "В собранном приложении веса обычно уже лежат в models/."
    )
    if not token:
        raise RuntimeError(f"Нет Hugging Face токена. {hint}")
    print(f"downloading {DIARIZATION_REPO} → {destination}", file=sys.stderr, flush=True)
    try:
        snapshot_download(
            repo_id=DIARIZATION_REPO,
            local_dir=destination,
            token=token,
        )
    except Exception as exc:
        raise RuntimeError(f"Не удалось скачать diarization: {exc}. {hint}") from exc
    if not diarization_ready(destination):
        raise RuntimeError(
            f"После загрузки нет нужных файлов в {destination}. Проверьте доступ к {DIARIZATION_REPO}."
        )
    print(f"diarization cached: {destination}", file=sys.stderr, flush=True)
    return destination


def load_diarization_pipeline():
    global _diarization_pipeline
    if _diarization_pipeline is not None:
        return _diarization_pipeline

    require_ffmpeg()
    from pyannote.audio import Pipeline
    import torch

    path = diarization_model_dir()
    if not diarization_ready(path):
        download_diarization_model()

    pipeline = Pipeline.from_pretrained(path)
    device = resolve_device()
    if device != "cpu":
        try:
            pipeline.to(torch.device(device))
        except Exception:
            print(
                f"Diarization на {device} недоступна, оставляю CPU",
                file=sys.stderr,
                flush=True,
            )
            traceback.print_exc(file=sys.stderr)
    _diarization_pipeline = pipeline
    print(f"diarization ready: {path} on {device}", file=sys.stderr, flush=True)
    return pipeline


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


def decode_with_ffmpeg(path: str) -> bytes:
    """Decode any ffmpeg-readable audio (wav/m4a/…) to 16 kHz mono PCM16."""
    require_ffmpeg()
    command = [
        "ffmpeg",
        "-nostdin",
        "-hide_banner",
        "-loglevel",
        "error",
        "-i",
        path,
        "-f",
        "s16le",
        "-acodec",
        "pcm_s16le",
        "-ac",
        "1",
        "-ar",
        str(SAMPLE_RATE),
        "pipe:1",
    ]
    try:
        completed = subprocess.run(
            command,
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError as exc:
        raise RuntimeError(f"Не удалось запустить ffmpeg: {exc}") from exc
    if completed.returncode != 0:
        detail = (completed.stderr or b"").decode("utf-8", errors="replace").strip()
        hint = detail or f"код {completed.returncode}"
        raise RuntimeError(f"Не удалось декодировать аудио ({os.path.basename(path)}): {hint}")
    return completed.stdout


def load_audio(path: str) -> bytes:
    """Load WAV via wave module when possible; otherwise decode with ffmpeg (m4a, …)."""
    if not path or not os.path.exists(path):
        raise RuntimeError("Аудиофайл не найден")
    if os.path.getsize(path) == 0:
        raise RuntimeError("Аудиофайл пуст")
    extension = os.path.splitext(path)[1].lower()
    if extension == ".wav":
        try:
            return read_wav(path)
        except (wave.Error, RuntimeError) as exc:
            print(f"wav read failed, ffmpeg fallback: {exc}", file=sys.stderr, flush=True)
    elif extension not in (".m4a", ".mp4", ".aac", ".mp3", ".flac", ".ogg", ".opus", ".caf", ".aiff", ".aif"):
        # Still try ffmpeg — it may recognize the container — but give a clearer first error.
        print(f"unusual audio extension: {extension or '(none)'}", file=sys.stderr, flush=True)
    return decode_with_ffmpeg(path)


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


def peak_frame_rms(pcm: bytes) -> int:
    frame_bytes = int(SAMPLE_RATE * FRAME_SECONDS) * 2
    if frame_bytes <= 0 or len(pcm) < frame_bytes:
        return frame_rms(pcm)
    peak = 0
    for offset in range(0, len(pcm) - frame_bytes + 1, frame_bytes):
        energy = frame_rms(pcm[offset : offset + frame_bytes])
        if energy > peak:
            peak = energy
    return peak


def speech_spans(pcm: bytes) -> list[tuple[float, float]]:
    frame_bytes = int(SAMPLE_RATE * FRAME_SECONDS) * 2
    if frame_bytes <= 0 or len(pcm) < frame_bytes:
        duration = (len(pcm) // 2) / SAMPLE_RATE
        # Короткие куски: только если есть реальная энергия, иначе тишина ≠ речь.
        if duration >= 0.25 and frame_rms(pcm) >= MIN_SPEECH_RMS:
            return [(0.0, min(duration, MAX_CHUNK_SECONDS))]
        return []

    energies = []
    for offset in range(0, len(pcm) - frame_bytes + 1, frame_bytes):
        energies.append(frame_rms(pcm[offset : offset + frame_bytes]))
    if not energies:
        return []

    ordered = sorted(energies)
    noise = ordered[max(0, int(len(ordered) * 0.2) - 1)]
    threshold = max(MIN_SPEECH_RMS, int(noise * 2.8))
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
    # Fallback только для тихой речи, которую VAD пропустил.
    # Цифровая тишина (system.wav из нулей) сюда НЕ попадает — иначе dual-track
    # ошибочно выбирается и всё помечается как me.
    if not merged and duration >= 0.25 and max(energies) >= MIN_SPEECH_RMS:
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


def speech_duration(pcm: bytes) -> float:
    return sum(end - start for start, end in speech_spans(pcm))


def has_meaningful_speech(pcm: bytes) -> bool:
    if peak_frame_rms(pcm) < MIN_SPEECH_RMS:
        return False
    return speech_duration(pcm) >= MIN_SPEECH_SECONDS


def load_track(path: str | None) -> bytes | None:
    if not path or not os.path.exists(path) or os.path.getsize(path) <= 44:
        return None
    try:
        pcm = load_audio(path)
    except RuntimeError as exc:
        print(f"load_track skip {path}: {exc}", file=sys.stderr, flush=True)
        return None
    if len(pcm) < int(SAMPLE_RATE * 0.2) * 2:
        return None
    return pcm


def mix_pcm(left: bytes, right: bytes) -> bytes:
    left_count = len(left) // 2
    right_count = len(right) // 2
    count = max(left_count, right_count)
    if count == 0:
        return b""
    left_samples = struct.unpack("<" + "h" * left_count, left[: left_count * 2]) if left_count else ()
    right_samples = struct.unpack("<" + "h" * right_count, right[: right_count * 2]) if right_count else ()
    mixed = []
    for index in range(count):
        a = left_samples[index] if index < left_count else 0
        b = right_samples[index] if index < right_count else 0
        mixed.append(int(max(-32768, min(32767, (a + b) // 2))))
    return struct.pack("<" + "h" * count, *mixed)


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
    pcm = load_audio(path)
    if len(pcm) < int(SAMPLE_RATE * 0.2) * 2:
        return ""
    parts = [segment["text"] for segment in transcribe_pcm(model, pcm, 0.0)]
    return " ".join(parts).strip()


def pcm_duration(pcm: bytes) -> float:
    return (len(pcm) // 2) / float(SAMPLE_RATE)


def transcribe_meeting_file(model, path: str, out_wav: str | None = None) -> tuple[list[dict], float]:
    """One mixed track → pyannote them1/them2/… → GigaAM. Same path as single-track calls."""
    pcm = load_audio(path)
    duration = pcm_duration(pcm)
    if len(pcm) < int(SAMPLE_RATE * 0.2) * 2:
        raise RuntimeError("Аудио слишком короткое или пустое")
    if out_wav:
        parent = os.path.dirname(out_wav)
        if parent:
            os.makedirs(parent, exist_ok=True)
        write_wav(out_wav, pcm)
        print(f"transcribe_file: wrote wav {out_wav} duration={duration:.1f}s", file=sys.stderr, flush=True)
    print(
        f"transcribe_file: diarization path={path} duration={duration:.1f}s",
        file=sys.stderr,
        flush=True,
    )
    try:
        segments = transcribe_call_diarized(model, pcm)
    except Exception as exc:
        print(f"transcribe_file diarization failed: {exc}", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        segments = transcribe_pcm(model, pcm, 0.0)
        for segment in segments:
            segment["speaker"] = "them1"
    return segments, duration


def slice_pcm(pcm: bytes, start: float, end: float) -> bytes:
    first = max(0, int(start * SAMPLE_RATE) * 2)
    last = min(len(pcm), int(end * SAMPLE_RATE) * 2)
    if last <= first:
        return b""
    return pcm[first:last]


def iter_diarization_turns(output) -> list[tuple[float, float, str]]:
    turns: list[tuple[float, float, str]] = []
    exclusive = getattr(output, "exclusive_speaker_diarization", None)
    if exclusive is not None:
        for turn, _, speaker in exclusive.itertracks(yield_label=True):
            turns.append((float(turn.start), float(turn.end), str(speaker)))
        if turns:
            return turns
    regular = getattr(output, "speaker_diarization", None)
    if regular is not None:
        for item in regular:
            if isinstance(item, (tuple, list)) and len(item) >= 2:
                turn, speaker = item[0], item[1]
                turns.append((float(turn.start), float(turn.end), str(speaker)))
        if turns:
            return turns
        if hasattr(regular, "itertracks"):
            for turn, _, speaker in regular.itertracks(yield_label=True):
                turns.append((float(turn.start), float(turn.end), str(speaker)))
    return turns


def run_diarization(pcm: bytes) -> list[tuple[float, float, str]]:
    pipeline = load_diarization_pipeline()
    import torch

    count = len(pcm) // 2
    if count == 0:
        return []
    samples = struct.unpack("<" + "h" * count, pcm[: count * 2])
    waveform = torch.tensor(samples, dtype=torch.float32).unsqueeze(0) / 32768.0
    output = pipeline({"waveform": waveform, "sample_rate": SAMPLE_RATE})
    turns = iter_diarization_turns(output)
    turns.sort(key=lambda item: (item[0], item[1]))
    speaker_map = map_speakers(turns)
    raw_ids = sorted({speaker for _, _, speaker in turns})
    print(
        f"diarization turns={len(turns)} raw_speakers={raw_ids} map={speaker_map}",
        file=sys.stderr,
        flush=True,
    )
    return turns


def map_speakers(turns: list[tuple[float, float, str]]) -> dict[str, str]:
    """Нумерует кластеры pyannote по первому появлению: them1, them2, …"""
    first_seen: dict[str, float] = {}
    for start, _, speaker in turns:
        if speaker not in first_seen:
            first_seen[speaker] = start
    ordered = sorted(first_seen.keys(), key=lambda speaker: (first_seen[speaker], speaker))
    return {speaker: f"them{index}" for index, speaker in enumerate(ordered, start=1)}


def transcribe_span(model, pcm: bytes, start: float, end: float) -> str:
    chunk = slice_pcm(pcm, start, end)
    if len(chunk) < int(SAMPLE_RATE * 0.2) * 2:
        return ""
    descriptor, path = tempfile.mkstemp(suffix=".wav", prefix="gigatts-")
    os.close(descriptor)
    try:
        write_wav(path, chunk)
        result = model.transcribe(path)
        return getattr(result, "text", str(result)).strip()
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def transcribe_diarized(model, pcm: bytes, turns: list[tuple[float, float, str]], speaker_map: dict[str, str]) -> list[dict]:
    collected: list[dict] = []
    for start, end, raw_speaker in turns:
        if end - start < 0.2:
            continue
        cursor = start
        while cursor < end:
            chunk_end = min(end, cursor + MAX_CHUNK_SECONDS)
            text = transcribe_span(model, pcm, cursor, chunk_end)
            if text:
                collected.append(
                    {
                        "start": round(cursor, 3),
                        "end": round(chunk_end, 3),
                        "text": text,
                        "speaker": speaker_map.get(raw_speaker, "them1"),
                    }
                )
            cursor = chunk_end
    collected.sort(key=lambda item: (item["start"], item["speaker"]))
    return collected


def _speaker_counts(segments: list[dict]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for segment in segments:
        speaker = str(segment.get("speaker") or "?")
        counts[speaker] = counts.get(speaker, 0) + 1
    return counts


def transcribe_call_dual(model, mic_pcm: bytes, system_pcm: bytes) -> list[dict]:
    collected = []
    for segment in transcribe_pcm(model, mic_pcm, 0.0):
        segment["speaker"] = "me"
        collected.append(segment)
    for segment in transcribe_pcm(model, system_pcm, 0.0):
        segment["speaker"] = "them"
        collected.append(segment)
    collected.sort(key=lambda item: (item["start"], 0 if item["speaker"] == "me" else 1))
    print(
        f"dual-track segments={len(collected)} speakers={_speaker_counts(collected)}",
        file=sys.stderr,
        flush=True,
    )
    return collected


def transcribe_call_diarized(model, audio_pcm: bytes) -> list[dict]:
    turns = run_diarization(audio_pcm)
    if not turns:
        # Pyannote ничего не нашёл — обычный VAD, один безымянный спикер.
        segments = transcribe_pcm(model, audio_pcm, 0.0)
        for segment in segments:
            segment["speaker"] = "them1"
        print(
            f"diarization empty → them1 segments={len(segments)}",
            file=sys.stderr,
            flush=True,
        )
        return segments
    speaker_map = map_speakers(turns)
    segments = transcribe_diarized(model, audio_pcm, turns, speaker_map)
    print(
        f"diarization segments={len(segments)} speakers={_speaker_counts(segments)}",
        file=sys.stderr,
        flush=True,
    )
    return segments


def transcribe_call(model, mic_path: str | None, system_path: str | None) -> list[dict]:
    mic_pcm = load_track(mic_path)
    system_pcm = load_track(system_path)
    mic_peak = peak_frame_rms(mic_pcm) if mic_pcm is not None else 0
    system_peak = peak_frame_rms(system_pcm) if system_pcm is not None else 0
    mic_speech = mic_pcm is not None and has_meaningful_speech(mic_pcm)
    system_speech = system_pcm is not None and has_meaningful_speech(system_pcm)

    # Две полезные дорожки: метки источника точнее акустической диаризации.
    if mic_speech and system_speech:
        print(
            "transcribe_call: dual-track (me/them) "
            f"mic_speech={speech_duration(mic_pcm):.1f}s "
            f"system_speech={speech_duration(system_pcm):.1f}s "
            f"mic_peak={mic_peak} system_peak={system_peak}",
            file=sys.stderr,
            flush=True,
        )
        return transcribe_call_dual(model, mic_pcm, system_pcm)

    # Любой single-track сценарий (mic-only, system-only, одна пустая) → pyannote
    # them1/them2/… Никогда не помечаем всё как me только потому что запись с микрофона.
    if mic_speech or system_speech or mic_pcm is not None or system_pcm is not None:
        if mic_speech and not system_speech:
            audio_pcm = mic_pcm
            source = "mic-only"
        elif system_speech and not mic_speech:
            audio_pcm = system_pcm
            source = "system-only"
        elif mic_pcm is not None and system_pcm is not None:
            audio_pcm = mix_pcm(mic_pcm, system_pcm)
            source = "mix"
        elif system_pcm is not None:
            audio_pcm = system_pcm
            source = "system"
        else:
            audio_pcm = mic_pcm
            source = "mic"
        print(
            f"transcribe_call: diarization ({source}) "
            f"mic_speech={mic_speech} system_speech={system_speech} "
            f"mic_peak={mic_peak} system_peak={system_peak}",
            file=sys.stderr,
            flush=True,
        )
        try:
            return transcribe_call_diarized(model, audio_pcm)
        except Exception as exc:
            print(f"diarization fallback failed: {exc}", file=sys.stderr, flush=True)
            traceback.print_exc(file=sys.stderr)
            segments = transcribe_pcm(model, audio_pcm, 0.0)
            for segment in segments:
                segment["speaker"] = "them1"
            return segments

    print("transcribe_call: empty (no tracks)", file=sys.stderr, flush=True)
    return []


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
    if command == "transcribe_file":
        segments, duration = transcribe_meeting_file(
            model,
            request.get("path") or "",
            request.get("out_wav") or None,
        )
        return model, {"segments": segments, "duration": round(duration, 3)}
    if command == "shutdown":
        return model, {}
    raise RuntimeError(f"Неизвестная команда: {command}")


def download_diarization_main() -> int:
    try:
        path = download_diarization_model()
        # stdout is redirected to stderr for the RPC mode; keep messages on stderr.
        print(f"OK {path}", file=sys.stderr, flush=True)
        return 0
    except Exception as exc:  # noqa: BLE001 - CLI boundary
        traceback.print_exc(file=sys.stderr)
        print(str(exc), file=sys.stderr, flush=True)
        return 1


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
    if len(sys.argv) > 1 and sys.argv[1] == "--download-diarization":
        raise SystemExit(download_diarization_main())
    raise SystemExit(main())
