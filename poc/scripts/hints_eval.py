"""Measures what a Whisper prompt does: names to spell (hints) and a sample of the dialect.

Usage:
  python hints_eval.py prepare SET [--distractors 8] [--seed 7]
  python hints_eval.py long SET LONG_SET [--per-file 25]
  python hints_eval.py noise NOISE_SET [--count 20] [--seconds 10]
  python hints_eval.py run --exe HINTS_EVAL_EXE --set SET --setup NAME [--prompts TSV | --prompt TEXT]
                           [--carry] [--language LANG] [--vad auto|on|off] [--model MODEL_FILE]
  python hints_eval.py score --set SET [--baseline none]

A test set is a folder with audio/*.wav and refs.tsv ("file name, tab, reference text" lines), as
fetch_testsets.py writes it on the poc/gemma-4-evaluation branch. Every folder this script writes
to must be one that git ignores, such as test-audio/.

`prepare` takes the names in each reference: the words that start with a capital letter and don't
start a sentence, with consecutive ones joined, such as "Hospital James Paget". It writes
SET/hints/terms.json and three prompt files: own.tsv (each recording's own names, the way a caller
who knows who and what the audio mentions would pass them), distractors.tsv (only names taken from
other recordings, the way a caller would pass a list where none of the names is in this audio), and
own+distractors.tsv (both, shuffled). Each recording keeps the same distractors in every file.

`long` joins the recordings of SET into files of about five minutes each, in LONG_SET, with each
file's names as hints. Whisper decodes 30-second windows, and by default only the first windows
read the prompt; the long files show whether carrying the prompt to every window matters.

`noise` writes recordings without speech (white noise, brown noise, hum, and room tone), which
show whether a prompt makes Whisper write text, or the prompt itself, where nobody speaks.

`run` transcribes SET/audio with the hints-eval program (poc/HintsEval) and writes the transcripts
to SET/hints-eval/transcripts/NAME/, and its timings to SET/hints-eval/results.jsonl.

`score` compares every run with the references: word error rate with a 95% interval from
resampling the recordings, the difference from the baseline run with its own interval (a
difference whose interval doesn't include zero isn't chance), how many of the names the
transcripts spell right, how many distractor names they write that nobody said, how many voseo
forms they keep, and how many recordings repeat a stretch of the dialect prompt.
"""
import argparse
import array
import json
import math
import random
import re
import subprocess
import sys
import unicodedata
import wave
from pathlib import Path

DEFAULT_MODEL = Path.home() / ".local-whisper-mcp" / "models" / "ggml-large-v3-turbo-q8_0.bin"

# A few lines of Rioplatense chat, written with voseo, as a sample of the dialect.
VOSEO_PROMPT = "Che, ¿vos sabés a qué hora venís mañana? Avisame cuando puedas y decime qué querés hacer, así lo charlamos."

# Names for the recordings without speech, where any of them in a transcript is an invention.
NOISE_HINTS = ["Orihuela", "Whisper.net", "Concentus", "María Fernanda López", "Villa Crespo", "MCPB"]

# Words that start a capitalized run but aren't names on their own.
NOT_NAMES = {"I"}


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


def count_sequence(words, sequence):
    """Counts the places where the word list contains the sequence."""
    n = len(sequence)
    return sum(1 for i in range(len(words) - n + 1) if words[i:i + n] == sequence) if n else 0


# Words that end like a voseo verb and aren't one, and "estás", which tuteo shares.
NOT_VOSEO = {
    "además", "atrás", "detrás", "demás", "jamás", "quizás", "más", "compás", "estás", "satanás",
    "después", "través", "interés", "revés", "estrés", "antiestrés", "cafés", "bebés", "inglés", "francés", "portugués",
    "japonés", "cortés", "holandés", "país", "parís", "anís",
}


def voseo_forms(words):
    """Returns the words that only voseo uses: 'vos', 'sos', and verbs such as 'podés'."""
    # Future forms such as "hablarás" and "podrás" end the same way and belong to tuteo too.
    return [
        word for word in words
        if word in ("vos", "sos")
        or (re.search(r"(ás|és|ís)$", word) and word not in NOT_VOSEO and not re.search(r"([aei]r|dr|br|rr)(ás|és)$", word))
    ]


def extract_names(text):
    """Returns the capitalized runs of words that don't start a sentence, in order, without repeats."""
    names = []
    for sentence in re.split(r"(?<=[.!?…])\s+|[¿¡]", text):
        tokens = re.findall(r"\w[\w'’.\-]*", sentence)
        run = []
        for index, token in enumerate(tokens + [""]):
            token = token.rstrip(".")
            if index > 0 and token[:1].isupper() and token not in NOT_NAMES:
                run.append(token)
                continue
            if run:
                name = " ".join(run)
                if len(name) > 1 and name not in names:
                    names.append(name)
                run = []
    return names


def read_refs(set_dir):
    refs = {}
    for line in (set_dir / "refs.tsv").read_text(encoding="utf-8").splitlines():
        if "\t" in line:
            name, text = line.split("\t", 1)
            refs[name] = text
    return refs


def write_prompts(path, prompts):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        for name, terms in prompts.items():
            if terms:
                file.write(f"{name}\t{hint_prompt(terms)}\n")


def hint_prompt(terms):
    """Builds the prompt the way the server's HintPrompt does: 'A, B, C.'."""
    prompt = ", ".join(terms)
    return prompt if prompt.endswith(".") else prompt + "."


def command_prepare(args):
    set_dir = Path(args.set)
    refs = read_refs(set_dir)
    own = {name: extract_names(text) for name, text in refs.items()}
    pool = sorted({term for terms in own.values() for term in terms})
    terms = {}
    for name, text in refs.items():
        reference = normalize(text)
        candidates = [term for term in pool if not count_sequence(reference, normalize(term))]
        chooser = random.Random(f"{args.seed}:{name}")
        distractors = chooser.sample(candidates, min(args.distractors, len(candidates)))
        terms[name] = {"own": own[name], "distractors": distractors}
    hints = set_dir / "hints"
    hints.mkdir(parents=True, exist_ok=True)
    (hints / "terms.json").write_text(json.dumps(terms, ensure_ascii=False, indent=1), encoding="utf-8")
    write_prompts(hints / "own.tsv", {name: entry["own"] for name, entry in terms.items()})
    write_prompts(hints / "distractors.tsv", {name: entry["distractors"] for name, entry in terms.items()})
    mixed = {}
    for name, entry in terms.items():
        both = entry["own"] + entry["distractors"]
        random.Random(f"{args.seed}:mix:{name}").shuffle(both)
        mixed[name] = both
    write_prompts(hints / "own+distractors.tsv", mixed)
    with_names = sum(1 for entry in terms.values() if entry["own"])
    lengths = sorted(len(hint_prompt(both)) for both in mixed.values() if both)
    print(f"{len(refs)} recordings, {with_names} with names, {sum(len(entry['own']) for entry in terms.values())} names in all.")
    print(f"Longest own+distractors prompt: {lengths[-1] if lengths else 0} characters; median {lengths[len(lengths) // 2] if lengths else 0}.")


def read_wave(path):
    with wave.open(str(path), "rb") as file:
        if file.getnchannels() != 1 or file.getsampwidth() != 2:
            raise SystemExit(f"{path.name}: expected 16-bit mono WAV")
        return file.getframerate(), file.readframes(file.getnframes())


def command_long(args):
    source = Path(args.set)
    target = Path(args.long_set)
    (target / "audio").mkdir(parents=True, exist_ok=True)
    refs = read_refs(source)
    names = list(refs)
    layout = {}
    long_refs = {}
    long_terms = {}
    for number, first in enumerate(range(0, len(names), args.per_file), 1):
        chunk = names[first:first + args.per_file]
        file_name = f"long-{number:02d}.wav"
        frames = bytearray()
        rate = None
        clips = []
        for name in chunk:
            clip_rate, data = read_wave(source / "audio" / name)
            if rate is None:
                rate = clip_rate
            elif clip_rate != rate:
                raise SystemExit(f"{name}: {clip_rate} Hz, while the others are {rate} Hz")
            start = len(frames) / 2 / rate
            frames += data
            clips.append({"clip": name, "start": round(start, 2), "names": extract_names(refs[name])})
            frames += bytes(int(rate * 0.5) * 2)
        with wave.open(str(target / "audio" / file_name), "wb") as file:
            file.setnchannels(1)
            file.setsampwidth(2)
            file.setframerate(rate)
            file.writeframes(bytes(frames))
        layout[file_name] = clips
        long_refs[file_name] = " ".join(refs[name] for name in chunk)
        terms = []
        for clip in clips:
            terms += [term for term in clip["names"] if term not in terms]
        long_terms[file_name] = {"own": terms, "distractors": []}
        print(f"{file_name}: {len(chunk)} recordings, {len(frames) / 2 / rate / 60:.1f} min, {len(terms)} names, prompt of {len(hint_prompt(terms)) if terms else 0} characters")
    (target / "refs.tsv").write_text("".join(f"{name}\t{text}\n" for name, text in long_refs.items()), encoding="utf-8")
    (target / "layout.json").write_text(json.dumps(layout, ensure_ascii=False, indent=1), encoding="utf-8")
    hints = target / "hints"
    hints.mkdir(exist_ok=True)
    (hints / "terms.json").write_text(json.dumps(long_terms, ensure_ascii=False, indent=1), encoding="utf-8")
    write_prompts(hints / "own.tsv", {name: entry["own"] for name, entry in long_terms.items()})


def command_noise(args):
    target = Path(args.noise_set)
    (target / "audio").mkdir(parents=True, exist_ok=True)
    rate = 16000
    count = rate * args.seconds
    kinds = ["white", "brown", "hum", "room"]
    for index in range(args.count):
        kind = kinds[index % len(kinds)]
        chooser = random.Random(index)
        if kind == "white":
            samples = [chooser.gauss(0, 0.05) for _ in range(count)]
        elif kind == "brown":
            level, samples = 0.0, []
            for _ in range(count):
                level = 0.995 * level + chooser.gauss(0, 0.02)
                samples.append(level)
        elif kind == "hum":
            samples = [0.05 * math.sin(2 * math.pi * 50 * i / rate) + 0.02 * math.sin(2 * math.pi * 150 * i / rate) + chooser.gauss(0, 0.005) for i in range(count)]
        else:
            samples = [chooser.gauss(0, 0.003) for _ in range(count)]
        pcm = array.array("h", (max(-32767, min(32767, int(sample * 32767))) for sample in samples))
        with wave.open(str(target / "audio" / f"noise-{index:02d}-{kind}.wav"), "wb") as file:
            file.setnchannels(1)
            file.setsampwidth(2)
            file.setframerate(rate)
            file.writeframes(pcm.tobytes())
    hints = target / "hints"
    hints.mkdir(exist_ok=True)
    names = sorted(path.name for path in (target / "audio").glob("*.wav"))
    write_prompts(hints / "distractors.tsv", {name: NOISE_HINTS for name in names})
    (hints / "terms.json").write_text(json.dumps({name: {"own": [], "distractors": NOISE_HINTS} for name in names}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{len(names)} recordings without speech in {target / 'audio'}")


def command_run(args):
    set_dir = Path(args.set)
    out = set_dir / "hints-eval"
    transcripts = out / "transcripts" / args.setup
    transcripts.mkdir(parents=True, exist_ok=True)
    for stale in transcripts.glob("*.txt"):
        stale.unlink()
    command = [args.exe, "--model", args.model, "--audio-dir", str(set_dir / "audio"), "--out-dir", str(transcripts), "--language", args.language, "--vad", args.vad]
    if args.prompts:
        command += ["--prompts", args.prompts]
    if args.prompt:
        command += ["--prompt", args.prompt]
    if args.carry:
        command.append("--carry")
    run = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if run.returncode != 0:
        sys.stderr.write(run.stderr[-3000:])
        raise SystemExit(f"hints-eval exited with {run.returncode}")
    metrics = json.loads(run.stdout[run.stdout.index("{"):])
    metrics.update({"setup": args.setup, "prompts": args.prompts, "prompt": args.prompt, "language": args.language, "model": Path(args.model).name})
    warnings = [line for line in run.stderr.splitlines() if "prompt" in line.lower()]
    if warnings:
        metrics["promptWarnings"] = warnings[:5]
    with open(out / "results.jsonl", "a", encoding="utf-8") as results:
        results.write(json.dumps(metrics, ensure_ascii=False) + "\n")
    print(json.dumps(metrics, ensure_ascii=False))


def percentile(values, fraction):
    return sorted(values)[min(len(values) - 1, int(fraction * len(values)))]


def command_score(args):
    set_dir = Path(args.set)
    out = set_dir / "hints-eval"
    terms = json.loads((set_dir / "hints" / "terms.json").read_text(encoding="utf-8")) if (set_dir / "hints" / "terms.json").exists() else {}
    runs = {}
    for line in (out / "results.jsonl").read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        runs[row["setup"]] = row
    has_refs = (set_dir / "refs.tsv").exists()
    if has_refs:
        refs = {name: normalize(text) for name, text in read_refs(set_dir).items()}
    else:
        refs = {path.name: [] for path in (set_dir / "audio").glob("*.wav")}
    names = sorted(refs)

    hypotheses = {}
    for folder in sorted(path for path in (out / "transcripts").iterdir() if path.is_dir()):
        if any(not (folder / f"{name}.txt").exists() for name in names):
            print(f"Skipping {folder.name}: it doesn't have a transcript for every recording.")
            continue
        hypotheses[folder.name] = {name: normalize((folder / f"{name}.txt").read_text(encoding="utf-8")) for name in names}
    setups = sorted(hypotheses, key=lambda setup: (setup != args.baseline, setup))
    if not setups:
        raise SystemExit("No complete runs to score.")

    voseo_words = normalize(VOSEO_PROMPT)
    voseo_grams = {tuple(voseo_words[i:i + 4]) for i in range(len(voseo_words) - 3)}

    def echoes(name, hypothesis):
        reference = refs[name]
        ref_grams = {tuple(reference[i:i + 4]) for i in range(len(reference) - 3)}
        return any(tuple(hypothesis[i:i + 4]) in voseo_grams and tuple(hypothesis[i:i + 4]) not in ref_grams for i in range(len(hypothesis) - 3))

    def term_hits(setup, kind):
        """Per recording and term: (in reference, written in the transcript)."""
        hits = {}
        for name in names:
            for term in terms.get(name, {}).get(kind, []):
                sequence = normalize(term)
                expected = count_sequence(refs[name], sequence)
                written = count_sequence(hypotheses[setup][name], sequence)
                hits[(name, term)] = (expected, written)
        return hits

    if not has_refs:
        print(f"\n{len(names)} recordings without speech\n")
        print("| Setup | Recordings with text | Words | Hint names written | Dialect prompt repeated | Seconds per audio minute |")
        print("|-------|----------------------|-------|--------------------|-------------------------|--------------------------|")
        for setup in setups:
            with_text = sum(1 for name in names if hypotheses[setup][name])
            words = sum(len(hypotheses[setup][name]) for name in names)
            invented = sum(written for (expected, written) in term_hits(setup, "distractors").values())
            repeated = sum(1 for name in names if echoes(name, hypotheses[setup][name]))
            speed = runs.get(setup, {}).get("secondsPerAudioMinute", "-")
            print(f"| {setup} | {with_text} of {len(names)} | {words} | {invented} | {repeated} | {speed} |")
        return

    scores = {setup: {name: word_errors(refs[name], hypotheses[setup][name]) for name in names} for setup in setups}
    with_numbers = {
        name for name in names
        if any(any(character.isdigit() for character in word) for words in [refs[name], *(hypotheses[setup][name] for setup in setups)] for word in words)
    }
    chooser = random.Random(0)
    samples = [[chooser.choice(names) for _ in names] for _ in range(args.resamples)]

    def rate(setup, chosen):
        return 100 * sum(scores[setup][name] for name in chosen) / max(1, sum(len(refs[name]) for name in chosen))

    print(f"\n{len(names)} recordings, {sum(len(words) for words in refs.values())} reference words, {len(with_numbers)} with numbers\n")
    print(f"| Setup | WER | 95% interval | WER without numbers | Difference from {args.baseline} | Voseo forms kept | Dialect prompt repeated | Seconds per audio minute |")
    print("|-------|-----|--------------|---------------------|" + "-" * (18 + len(args.baseline)) + "|------------------|-------------------------|--------------------------|")
    for setup in setups:
        resampled = [rate(setup, chosen) for chosen in samples]
        if setup == args.baseline or args.baseline not in scores:
            difference = "-"
        else:
            deltas = [rate(setup, chosen) - rate(args.baseline, chosen) for chosen in samples]
            difference = f"{rate(setup, names) - rate(args.baseline, names):+.2f} ({percentile(deltas, 0.025):+.2f} to {percentile(deltas, 0.975):+.2f})"
        total = kept = 0
        for name in names:
            hypothesis = list(hypotheses[setup][name])
            for form in voseo_forms(refs[name]):
                total += 1
                if form in hypothesis:
                    hypothesis.remove(form)
                    kept += 1
        voseo = f"{kept} of {total}" if total else "-"
        repeated = sum(1 for name in names if echoes(name, hypotheses[setup][name]))
        speed = runs.get(setup, {}).get("secondsPerAudioMinute", "-")
        print(
            f"| {setup} | {rate(setup, names):.2f}% | {percentile(resampled, 0.025):.2f} to {percentile(resampled, 0.975):.2f} "
            f"| {rate(setup, [name for name in names if name not in with_numbers]):.2f}% | {difference} | {voseo} | {repeated} | {speed} |")

    if not terms:
        return

    # Names: an occurrence counts as spelled right when the transcript has as many as the reference.
    baseline_own = term_hits(args.baseline, "own") if args.baseline in hypotheses else None
    print("\n| Setup | Names spelled right | Fixed from baseline | Broken from baseline | Distractor names written, not said |")
    print("|-------|---------------------|---------------------|----------------------|------------------------------------|")
    for setup in setups:
        own = term_hits(setup, "own")
        expected = sum(e for (e, w) in own.values())
        right = sum(min(e, w) for (e, w) in own.values())
        fixed = broken = 0
        if baseline_own is not None:
            for key, (e, w) in own.items():
                before = min(e, baseline_own[key][1]) == e
                after = min(e, w) == e
                fixed += after and not before
                broken += before and not after
        invented = sum(w for (e, w) in term_hits(setup, "distractors").values() if e == 0)
        print(f"| {setup} | {right} of {expected} ({100 * right / max(1, expected):.1f}%) | {fixed} | {broken} | {invented} |")

    if baseline_own is not None:
        print("\nNames the baseline misspells (up to 15), and the runs that spell them right:")
        shown = 0
        for (name, term), (e, w) in baseline_own.items():
            if w >= e or shown >= 15:
                continue
            shown += 1
            fixed_by = [setup for setup in setups if setup != args.baseline and count_sequence(hypotheses[setup][name], normalize(term)) >= e]
            print(f"- {name}: \"{term}\" fixed by {', '.join(fixed_by) or 'none'}")

    layout_path = set_dir / "layout.json"
    if layout_path.exists():
        # In the long files, a name that appears in only one recording of its file shows where in
        # the file Whisper still reads the prompt.
        layout = json.loads(layout_path.read_text(encoding="utf-8"))
        buckets = [("first 30 s", 0, 30), ("30 s to 2 min", 30, 120), ("after 2 min", 120, 10 ** 9)]
        print("\n| Setup | " + " | ".join(f"Names {label}" for label, _, _ in buckets) + " |")
        print("|-------|" + "|".join("-" * 20 for _ in buckets) + "|")
        for setup in setups:
            cells = []
            for label, low, high in buckets:
                expected = right = 0
                for file_name, clips in layout.items():
                    for clip in clips:
                        if not low <= clip["start"] < high:
                            continue
                        for term in clip["names"]:
                            sequence = normalize(term)
                            if count_sequence(refs[file_name], sequence) != 1:
                                continue
                            expected += 1
                            right += count_sequence(hypotheses[setup][file_name], sequence) >= 1
                cells.append(f"{right} of {expected} ({100 * right / max(1, expected):.1f}%)")
            print(f"| {setup} | " + " | ".join(cells) + " |")


parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
commands = parser.add_subparsers(dest="command", required=True)
prepare_parser = commands.add_parser("prepare")
prepare_parser.add_argument("set")
prepare_parser.add_argument("--distractors", type=int, default=8)
prepare_parser.add_argument("--seed", default="7")
long_parser = commands.add_parser("long")
long_parser.add_argument("set")
long_parser.add_argument("long_set")
long_parser.add_argument("--per-file", type=int, default=25)
noise_parser = commands.add_parser("noise")
noise_parser.add_argument("noise_set")
noise_parser.add_argument("--count", type=int, default=20)
noise_parser.add_argument("--seconds", type=int, default=10)
run_parser = commands.add_parser("run")
run_parser.add_argument("--exe", required=True)
run_parser.add_argument("--set", required=True)
run_parser.add_argument("--setup", required=True)
run_parser.add_argument("--prompts")
run_parser.add_argument("--prompt")
run_parser.add_argument("--carry", action="store_true")
run_parser.add_argument("--language", default="auto")
run_parser.add_argument("--vad", default="auto", choices=["auto", "on", "off"])
run_parser.add_argument("--model", default=str(DEFAULT_MODEL))
score_parser = commands.add_parser("score")
score_parser.add_argument("--set", required=True)
score_parser.add_argument("--baseline", default="none")
score_parser.add_argument("--resamples", type=int, default=2000)

arguments = parser.parse_args()
{"prepare": command_prepare, "long": command_long, "noise": command_noise, "run": command_run, "score": command_score}[arguments.command](arguments)
