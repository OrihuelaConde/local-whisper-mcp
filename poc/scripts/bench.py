"""Runs AotWhisper once and records its metrics plus the GPU memory used while it runs.

Usage: python bench.py AOTWHISPER_EXE [AotWhisper arguments...]

GPU memory comes from nvidia-smi, sampled every 100 ms, as the peak over the run minus the
reading before the process starts. It measures the whole GPU, so other processes that allocate
memory during the run distort it. Prints one JSON object on stdout.
"""
import json
import re
import shutil
import subprocess
import sys
import threading
import time


def gpu_used_mib():
    if not shutil.which("nvidia-smi"):
        return None
    output = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
        capture_output=True,
        text=True,
    ).stdout
    return int(output.split()[0]) if output.strip() else None


baseline = gpu_used_mib()
peak = baseline
stop = threading.Event()


def sample():
    global peak
    while not stop.is_set():
        used = gpu_used_mib()
        if used is not None and (peak is None or used > peak):
            peak = used
        time.sleep(0.1)


sampler = threading.Thread(target=sample, daemon=True)
sampler.start()
start = time.perf_counter()
run = subprocess.run(sys.argv[1:], capture_output=True, text=True, encoding="utf-8")
wall = time.perf_counter() - start
stop.set()
sampler.join()

metrics = {}
for line in run.stderr.splitlines():
    if match := re.match(r"\[metrics\] runtime library: (\w+)", line):
        metrics["runtime"] = match.group(1)
    elif match := re.match(r"\[metrics\] model load: ([\d.]+) s, transcribe \(with vad\): ([\d.]+) s", line):
        metrics["load_s"], metrics["transcribe_s"] = float(match.group(1)), float(match.group(2))
    elif match := re.match(r"\[metrics\] seconds per audio minute: ([\d.]+)", line):
        metrics["s_per_audio_min"] = float(match.group(1))
    elif match := re.match(r"\[metrics\] peak working set: (\d+) MiB", line):
        metrics["peak_ram_mib"] = int(match.group(1))
    elif match := re.match(r"\[metrics\] vad: (\d+) speech spans, ([\d.]+) s", line):
        metrics["vad_spans"], metrics["speech_s"] = int(match.group(1)), float(match.group(2))

metrics["wall_s"] = round(wall, 2)
metrics["vram_delta_mib"] = None if baseline is None else peak - baseline
metrics["exit_code"] = run.returncode
metrics["transcript"] = run.stdout.strip()
if run.returncode != 0:
    metrics["stderr_tail"] = run.stderr[-2000:]
print(json.dumps(metrics, ensure_ascii=False))
