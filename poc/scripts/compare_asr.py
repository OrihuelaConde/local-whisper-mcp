"""Runs gemma-asr on audio files with several engine setups and compares the transcripts.

Usage:
  python compare_asr.py run --exe GEMMA_ASR_EXE --out DIR --audio FILE... --setup NAME...
                            [--language LANG] [--limit-seconds N] [--tag TAG]
  python compare_asr.py report --out DIR [--baseline NAME]

`run` executes each setup on each file, one process per run, and appends one JSON line per run
to DIR/results.jsonl. Transcripts go to DIR/transcripts/. DIR must be a folder that git ignores,
because the recordings can be private. GPU memory comes from nvidia-smi, sampled every 100 ms, as
the peak over the run minus the reading before the process starts; it measures the whole GPU, so
other processes that allocate memory during the run distort it.

`report` prints one Markdown table per file. The word error rate compares each transcript with
DIR/refs/<file name>.txt when that reference exists, and with the baseline setup's transcript
otherwise, in which case it measures disagreement with the baseline, not errors. "In loops" is
the share of the words that sit inside repetition loops, which both engines produce on noise.
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
import threading
import time
import unicodedata
from pathlib import Path

HOME = Path.home()
WHISPER_MODELS = HOME / ".local-whisper-mcp" / "models"
GEMMA_MODELS = Path(__file__).resolve().parents[2] / "models" / "gemma"

# name -> gemma-asr arguments
SETUPS = {
    "whisper-gpu": ["--engine", "whisper", "--model", str(WHISPER_MODELS / "ggml-large-v3-turbo-q8_0.bin"), "--backend", "gpu"],
    "whisper-cpu": ["--engine", "whisper", "--model", str(WHISPER_MODELS / "ggml-large-v3-turbo-q8_0.bin"), "--backend", "cpu"],
    "gemma-e2b-gpu": ["--engine", "gemma", "--model", str(GEMMA_MODELS / "gemma-4-E2B-it.litertlm"), "--backend", "gpu"],
    "gemma-e2b-cpu": ["--engine", "gemma", "--model", str(GEMMA_MODELS / "gemma-4-E2B-it.litertlm"), "--backend", "cpu"],
    "gemma-e4b-gpu": ["--engine", "gemma", "--model", str(GEMMA_MODELS / "gemma-4-E4B-it.litertlm"), "--backend", "gpu"],
    "gemma-e4b-cpu": ["--engine", "gemma", "--model", str(GEMMA_MODELS / "gemma-4-E4B-it.litertlm"), "--backend", "cpu"],
}


def gpu_used_mib():
    if not shutil.which("nvidia-smi"):
        return None
    output = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
        capture_output=True,
        text=True,
    ).stdout
    return int(output.split()[0]) if output.strip() else None


def run_once(command):
    """Runs one gemma-asr process and returns its metrics plus the GPU memory it added."""
    baseline = gpu_used_mib()
    peak = [baseline]
    stop = threading.Event()

    def sample():
        while not stop.is_set():
            used = gpu_used_mib()
            if used is not None and (peak[0] is None or used > peak[0]):
                peak[0] = used
            time.sleep(0.1)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    start = time.perf_counter()
    run = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    wall = time.perf_counter() - start
    stop.set()
    sampler.join()

    if run.returncode != 0:
        return {"exitCode": run.returncode, "stderrTail": run.stderr[-1500:], "wallSeconds": round(wall, 2)}
    metrics = json.loads(run.stdout[run.stdout.index("{"):])
    metrics["wallSeconds"] = round(wall, 2)
    metrics["vramDeltaMiB"] = None if baseline is None else peak[0] - baseline
    return metrics


def command_run(args):
    out = Path(args.out)
    transcripts = out / "transcripts"
    transcripts.mkdir(parents=True, exist_ok=True)
    for audio in args.audio:
        for name in args.setup:
            label = name + (f"+{args.tag}" if args.tag else "")
            transcript = transcripts / f"{Path(audio).name}.{label}.txt"
            command = [args.exe, *SETUPS[name], "--audio", audio, "--language", args.language, "--out", str(transcript), *args.extra]
            if args.limit_seconds:
                command += ["--limit-seconds", str(args.limit_seconds)]
            metrics = run_once(command)
            metrics.update({"setup": label, "audio": Path(audio).name})
            with open(out / "results.jsonl", "a", encoding="utf-8") as results:
                results.write(json.dumps(metrics, ensure_ascii=False) + "\n")
            summary = {key: metrics.get(key) for key in ("audioSeconds", "loadSeconds", "transcribeSeconds", "secondsPerAudioMinute", "peakWorkingSetMiB", "vramDeltaMiB", "words", "exitCode")}
            print(f"{Path(audio).name} | {label} | {json.dumps(summary)}", flush=True)


def normalize(text):
    """Lowercases, drops punctuation, and keeps accents, so 'confirmame' and 'confírmame' differ."""
    text = unicodedata.normalize("NFC", text.lower())
    text = re.sub(r"(?<=\d)[.,:](?=\d)", "", text)
    return re.sub(r"[^\w\s]", " ", text).split()


def word_errors(reference, hypothesis):
    """Returns the word-level edit distance between two word lists."""
    previous = list(range(len(hypothesis) + 1))
    for i, expected in enumerate(reference, 1):
        current = [i] + [0] * len(hypothesis)
        for j, actual in enumerate(hypothesis, 1):
            current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (expected != actual))
        previous = current
    return previous[-1]


def loop_words(words):
    """Counts the words inside repetition loops: a unit of one to six words said four or more times in a row."""
    looped = 0
    i = 0
    while i < len(words):
        longest = 0
        for size in range(1, 7):
            unit = words[i:i + size]
            repeats = 1
            while words[i + repeats * size:i + (repeats + 1) * size] == unit:
                repeats += 1
            if repeats >= 4:
                longest = max(longest, repeats * size)
        looped += longest
        i += longest or 1
    return looped


def command_report(args):
    out = Path(args.out)
    latest = {}
    for line in (out / "results.jsonl").read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        latest[(row["audio"], row["setup"])] = row
    for audio in sorted({key[0] for key in latest}):
        rows = [row for (name, _), row in latest.items() if name == audio and "exitCode" not in row]
        reference_file = out / "refs" / f"{audio}.txt"
        baseline_file = out / "transcripts" / f"{audio}.{args.baseline}.txt"
        if reference_file.exists():
            reference, against = normalize(reference_file.read_text(encoding="utf-8")), "reference"
        elif baseline_file.exists():
            reference, against = normalize(baseline_file.read_text(encoding="utf-8")), args.baseline
        else:
            reference, against = None, "nothing"
        baseline_seconds = latest.get((audio, args.baseline), {}).get("audioSeconds")
        print(f"\n### {audio} ({rows[0]['audioSeconds']:.0f} s), word error rate against {against}\n")
        print("| Setup | Load | Transcribe | Seconds per audio minute | Cores busy | Peak RAM | VRAM | Words | In loops | WER |")
        print("|-------|------|------------|--------------------------|------------|----------|------|-------|----------|-----|")
        for row in sorted(rows, key=lambda row: row["setup"]):
            words = normalize((out / "transcripts" / f"{audio}.{row['setup']}.txt").read_text(encoding="utf-8"))

            # A transcript of a different stretch of the audio, such as a --limit-seconds run, has no fair comparison.
            comparable = reference is not None and (against == "reference" or row["audioSeconds"] == baseline_seconds)
            wer = f"{100 * word_errors(reference, words) / max(1, len(reference)):.1f}%" if comparable else "-"
            cores = f"{row['processorSeconds'] / row['wallSeconds']:.1f}" if "processorSeconds" in row else "-"
            vram = "-" if not row.get("vramDeltaMiB") or row["vramDeltaMiB"] < 100 else f"{row['vramDeltaMiB'] / 1024:.1f} GiB"
            print(
                f"| {row['setup']} | {row['loadSeconds']:.1f} s | {row['transcribeSeconds']:.1f} s | {row['secondsPerAudioMinute']:.2f} | {cores} "
                f"| {row['peakWorkingSetMiB'] / 1024:.1f} GiB | {vram} | {len(words)} | {100 * loop_words(words) / max(1, len(words)):.1f}% | {wer} |")


parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
commands = parser.add_subparsers(dest="command", required=True)
run_parser = commands.add_parser("run")
run_parser.add_argument("--exe", required=True)
run_parser.add_argument("--out", required=True)
run_parser.add_argument("--audio", nargs="+", required=True)
run_parser.add_argument("--setup", nargs="+", required=True, choices=sorted(SETUPS))
run_parser.add_argument("--language", default="auto")
run_parser.add_argument("--limit-seconds", type=float, default=0)
run_parser.add_argument("--tag", default="")
run_parser.add_argument("extra", nargs="*", help="Extra gemma-asr arguments, after --.")
report_parser = commands.add_parser("report")
report_parser.add_argument("--out", required=True)
report_parser.add_argument("--baseline", default="whisper-gpu")
arguments = parser.parse_args()
sys.stdout.reconfigure(encoding="utf-8")
{"run": command_run, "report": command_report}[arguments.command](arguments)
