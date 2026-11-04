"""Transcribes a folder of audio files with transcribe.cpp and writes what compare_asr.py scores.

Usage:
  python tcpp_transcribe.py --model GGUF --setup NAME --out DIR (--audio-dir FOLDER | --audio FILE)
                            [--device gpu|cpu] [--language LANG|auto] [--threads N]

Each run is one process that loads the model once, so its load time and memory peaks belong to that
run alone. Transcripts go to DIR/transcripts/NAME/<file name>.txt, and one JSON line goes to
DIR/results.jsonl with the fields compare_asr.py reads. DIR must be a folder that git ignores,
because the recordings can be private.

GPU memory comes from nvidia-smi, sampled every 100 ms, as the peak over the run minus the reading
before the model loads. It measures the whole GPU, so other processes distort it.
"""
import argparse
import collections
import ctypes
import json
import os
import shutil
import subprocess
import threading
import time
from ctypes import wintypes
from pathlib import Path

import numpy as np
import soundfile
import transcribe_cpp

SAMPLE_RATE = 16000


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def peak_memory_mib():
    """Returns the process's peak working set and peak commit, or (None, None) outside Windows."""
    if os.name != "nt":
        return None, None
    counters = ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    psapi = ctypes.WinDLL("psapi")
    kernel32 = ctypes.WinDLL("kernel32")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
    return counters.PeakWorkingSetSize // 2**20, counters.PeakPagefileUsage // 2**20


def gpu_used_mib():
    if not shutil.which("nvidia-smi"):
        return None
    output = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
        capture_output=True,
        text=True,
    ).stdout
    return int(output.split()[0]) if output.strip() else None


class GpuMemorySampler:
    """Tracks the highest GPU memory reading in a background thread."""

    def __init__(self):
        self.baseline = gpu_used_mib()
        self.peak = self.baseline
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._sample, daemon=True)

    def _sample(self):
        while not self._stop.is_set():
            used = gpu_used_mib()
            if used is not None and (self.peak is None or used > self.peak):
                self.peak = used
            time.sleep(0.1)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        self._thread.join()

    def delta(self):
        return None if self.baseline is None else self.peak - self.baseline


def resample(samples, source_rate):
    """Applies the server's windowed-sinc filter (Audio/Resampler.cs), so every engine gets the same samples."""
    half_taps = 16
    ratio = source_rate / SAMPLE_RATE
    cutoff = min(1.0, 1.0 / ratio)
    centers = np.arange(int(len(samples) / ratio)) * ratio
    first = np.floor(centers).astype(np.int64) - half_taps + 1
    taps = first[:, None] + np.arange(2 * half_taps)
    offsets = centers[:, None] - taps
    window_position = np.clip(offsets / half_taps, -1, 1)
    t = (window_position + 1) / 2
    window = 0.42 - 0.5 * np.cos(2 * np.pi * t) + 0.08 * np.cos(4 * np.pi * t)
    window[np.abs(offsets / half_taps) >= 1] = 0
    weights = cutoff * np.sinc(cutoff * offsets) * window
    inside = (taps >= 0) & (taps < len(samples))
    values = np.where(inside, samples[np.clip(taps, 0, len(samples) - 1)], 0.0)
    return (values * weights).sum(axis=1).astype(np.float32)


def read_pcm(path):
    """Reads a file as 16 kHz mono float32 samples."""
    samples, rate = soundfile.read(path, dtype="float32", always_2d=True)
    samples = samples.mean(axis=1)
    return samples if rate == SAMPLE_RATE else resample(samples, rate)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--model", required=True)
    parser.add_argument("--setup", required=True, help="Name of the transcript folder and the results row.")
    parser.add_argument("--out", required=True)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--audio-dir")
    source.add_argument("--audio")
    parser.add_argument("--device", choices=["gpu", "cpu"], default="gpu")
    parser.add_argument("--language", default="auto", help="Language code, or auto for the model's own detection.")
    parser.add_argument("--threads", type=int, default=8, help="CPU threads; 8 is the server's Whisper default.")
    args = parser.parse_args()

    out = Path(args.out)
    transcripts = out / "transcripts" / args.setup
    transcripts.mkdir(parents=True, exist_ok=True)
    files = sorted(Path(args.audio_dir).glob("*.wav")) if args.audio_dir else [Path(args.audio)]
    language = None if args.language == "auto" else args.language

    started = time.perf_counter()
    start_times = os.times()
    decode_seconds = audio_seconds = transcribe_seconds = 0.0
    words = truncated = failed = 0
    detected = collections.Counter()
    timestamp_kinds = collections.Counter()
    failures = []

    with GpuMemorySampler() as gpu:
        device = next(d for d in transcribe_cpp.backends() if d.device_type == args.device)
        load_start = time.perf_counter()
        with transcribe_cpp.Model(args.model, device=device) as model:
            with model.session(n_threads=args.threads) as session:
                load_seconds = time.perf_counter() - load_start
                backend = f"{device.device_type}:{device.name}"
                for path in files:
                    decode_start = time.perf_counter()
                    pcm = read_pcm(path)
                    decode_seconds += time.perf_counter() - decode_start
                    audio_seconds += len(pcm) / SAMPLE_RATE

                    run_start = time.perf_counter()
                    try:
                        result = session.run(pcm, language=language)
                    except transcribe_cpp.OutputTruncated as error:
                        result = error.partial_result
                        truncated += 1
                    except transcribe_cpp.TranscribeError as error:
                        result = None
                        failed += 1
                        failures.append(f"{path.name}: {type(error).__name__}: {error}")
                    transcribe_seconds += time.perf_counter() - run_start

                    text = result.text.strip() if result else ""
                    if result:
                        detected[result.language] += 1
                        timestamp_kinds[result.timestamp_kind] += 1
                    words += len(text.split())
                    (transcripts / f"{path.name}.txt").write_text(text, encoding="utf-8")

    end_times = os.times()
    peak_working_set, peak_commit = peak_memory_mib()
    row = {
        "engine": "transcribe.cpp",
        "engineVersion": transcribe_cpp.native_version(),
        "model": Path(args.model).name,
        "audio": Path(args.audio_dir).parent.name if args.audio_dir else Path(args.audio).name,
        "files": len(files),
        "backend": backend,
        "threads": args.threads,
        "language": args.language,
        "detectedLanguages": dict(detected),
        "timestampKinds": dict(timestamp_kinds),
        "audioSeconds": round(audio_seconds, 2),
        "decodeSeconds": round(decode_seconds, 3),
        "loadSeconds": round(load_seconds, 2),
        "transcribeSeconds": round(transcribe_seconds, 2),
        "secondsPerAudioMinute": round(60 * transcribe_seconds / max(audio_seconds, 1e-9), 2),
        "processorSeconds": round(end_times.user + end_times.system - start_times.user - start_times.system, 1),
        "peakWorkingSetMiB": peak_working_set,
        "peakCommitMiB": peak_commit,
        "vramDeltaMiB": gpu.delta(),
        "vramBaselineMiB": gpu.baseline,
        "words": words,
        "truncated": truncated,
        "failed": failed,
        "failures": failures[:20],
        "wallSeconds": round(time.perf_counter() - started, 2),
        "setup": args.setup,
    }
    with open(out / "results.jsonl", "a", encoding="utf-8") as results:
        results.write(json.dumps(row, ensure_ascii=False) + "\n")
    print(json.dumps(row, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
