"""Mixes background noise into a test set at fixed signal-to-noise ratios and scores the transcripts.

Usage:
  python tcpp_noise.py make --set SET --noise-pool POOL_AUDIO_DIR --out-root DIR [--snr 20 10 5 0]
  python tcpp_noise.py score --out-root DIR [--baseline whisper] [--other parakeet-ultra] [--resamples 2000]

`make` writes DIR/snrNN/audio/, with the same file names as SET/audio, and copies SET/refs.tsv next
to it. Each recording gets one kind of noise, in turn: white, pink, or babble, which sums eight
stretches of the recordings in POOL_AUDIO_DIR. A recording gets the same noise at every ratio, so
only the level changes. The ratio compares the root mean square of the whole recording with that of
the noise. DIR must be a folder that git ignores, because the recordings can be private.

`score` reads DIR/snrNN/hints-eval/transcripts/SETUP/ and prints, for each ratio and kind of noise,
the word error rate of both setups and their difference, with 95% intervals from resampling the
recordings.
"""
import argparse
import random
import re
import shutil
import unicodedata
import zlib
from pathlib import Path

KINDS = ["white", "pink", "babble"]
BABBLE_VOICES = 8


def pink_noise(rng, count):
    """Returns noise whose power falls as 1/f, shaped in the frequency domain."""
    spectrum = np.fft.rfft(rng.standard_normal(count))
    frequencies = np.fft.rfftfreq(count)
    frequencies[0] = frequencies[1]
    return np.fft.irfft(spectrum / np.sqrt(frequencies), count)


def babble_noise(rng, count, pool):
    """Returns the sum of several stretches of speech from random places in the pool."""
    total = np.zeros(count)
    for _ in range(BABBLE_VOICES):
        start = int(rng.integers(0, len(pool) - count))
        total += pool[start:start + count]
    return total


def rms(samples):
    return np.sqrt(np.mean(np.square(samples.astype(np.float64))))


def command_make(args):
    source = Path(args.set)
    # Silence between pool recordings would thin the babble, so they join with no gap.
    pool = np.concatenate([soundfile.read(path, dtype="float32")[0] for path in sorted(Path(args.noise_pool).glob("*.wav"))])
    names = sorted(path.name for path in (source / "audio").glob("*.wav"))
    for snr in args.snr:
        target = Path(args.out_root) / f"snr{snr:02d}"
        (target / "audio").mkdir(parents=True, exist_ok=True)
        shutil.copy(source / "refs.tsv", target / "refs.tsv")
        for index, name in enumerate(names):
            speech, rate = soundfile.read(source / "audio" / name, dtype="float32")
            rng = np.random.default_rng(zlib.crc32(name.encode()))
            kind = KINDS[index % len(KINDS)]
            if kind == "white":
                noise = rng.standard_normal(len(speech))
            elif kind == "pink":
                noise = pink_noise(rng, len(speech))
            else:
                noise = babble_noise(rng, len(speech), pool)
            mixed = speech + noise * rms(speech) / (rms(noise) * 10 ** (snr / 20))
            peak = np.abs(mixed).max()
            if peak > 0.99:
                mixed = mixed * 0.99 / peak
            soundfile.write(target / "audio" / name, mixed, rate, subtype="PCM_16")
        print(f"{target}: {len(names)} recordings", flush=True)


def normalize(text):
    """Lowercases, drops punctuation, and keeps accents, as compare_asr.py does."""
    text = unicodedata.normalize("NFC", text.lower())
    text = re.sub(r"(?<=\d)[.,:](?=\d)", "", text)
    return re.sub(r"[^\w\s]", " ", text).split()


def word_errors(reference, hypothesis):
    previous = list(range(len(hypothesis) + 1))
    for i, expected in enumerate(reference, 1):
        current = [i] + [0] * len(hypothesis)
        for j, actual in enumerate(hypothesis, 1):
            current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (expected != actual))
        previous = current
    return previous[-1]


def percentile(values, fraction):
    return sorted(values)[min(len(values) - 1, int(fraction * len(values)))]


def command_score(args):
    print(f"| SNR | Noise | {args.baseline} | {args.other} | Difference from {args.baseline} | Large failures of {args.other} | Large failures of {args.baseline} |")
    print("|-----|-------|---|---|---|---|---|")
    for folder in sorted(Path(args.out_root).glob("snr*"), key=lambda path: -int(path.name[3:])):
        lines = (folder / "refs.tsv").read_text(encoding="utf-8").splitlines()
        refs = {name: normalize(text) for name, text in (line.split("\t", 1) for line in lines)}
        names = sorted(refs)
        kinds = {name: KINDS[index % len(KINDS)] for index, name in enumerate(names)}
        scores = {}
        for setup in (args.baseline, args.other):
            transcripts = folder / "hints-eval" / "transcripts" / setup
            scores[setup] = {name: word_errors(refs[name], normalize((transcripts / f"{name}.txt").read_text(encoding="utf-8"))) for name in names}

        def rate(setup, chosen):
            return 100 * sum(scores[setup][name] for name in chosen) / max(1, sum(len(refs[name]) for name in chosen))

        def large(setup, against, chosen):
            return sum(1 for name in chosen if scores[setup][name] >= 2 * scores[against][name] and scores[setup][name] >= scores[against][name] + 20)

        chooser = random.Random(0)
        for kind in ["all", *KINDS]:
            chosen = [name for name in names if kind in ("all", kinds[name])]
            samples = [[chooser.choice(chosen) for _ in chosen] for _ in range(args.resamples)]
            cells = []
            for setup in (args.baseline, args.other):
                rates = [rate(setup, sample) for sample in samples]
                cells.append(f"{rate(setup, chosen):.1f}% ({percentile(rates, 0.025):.1f} to {percentile(rates, 0.975):.1f})")
            deltas = [rate(args.other, sample) - rate(args.baseline, sample) for sample in samples]
            difference = f"{rate(args.other, chosen) - rate(args.baseline, chosen):+.1f} ({percentile(deltas, 0.025):+.1f} to {percentile(deltas, 0.975):+.1f})"
            print(f"| {folder.name[3:]} dB | {kind} | {cells[0]} | {cells[1]} | {difference} | {large(args.other, args.baseline, chosen)} | {large(args.baseline, args.other, chosen)} |")


parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
commands = parser.add_subparsers(dest="command", required=True)
make_parser = commands.add_parser("make")
make_parser.add_argument("--set", required=True)
make_parser.add_argument("--noise-pool", required=True)
make_parser.add_argument("--out-root", required=True)
make_parser.add_argument("--snr", type=int, nargs="+", default=[20, 10, 5, 0])
score_parser = commands.add_parser("score")
score_parser.add_argument("--out-root", required=True)
score_parser.add_argument("--baseline", default="whisper")
score_parser.add_argument("--other", default="parakeet-ultra")
score_parser.add_argument("--resamples", type=int, default=2000)
arguments = parser.parse_args()
if arguments.command == "make":
    import numpy as np
    import soundfile
    command_make(arguments)
else:
    command_score(arguments)
