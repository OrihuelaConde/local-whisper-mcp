# Hints: names as a Whisper prompt

This page records what the `hints` parameter of the `transcribe` tool does to transcripts. Whisper
reads a prompt as if it were the transcript of the audio that came before the recording, so a list
of the names that a recording mentions nudges Whisper toward those spellings. The server joins the
hints into a prompt such as `Mirtha Legrand, Whisper.net, MCPB.` and passes it to every 30-second
window that Whisper decodes.

## Verdict

Hints are worth passing when the caller knows the names in the recording, within a limit of 120
characters.

- **Names come out right more often.** On FLEURS Spanish, the share of names spelled as in the
  reference goes from 80.6% to 90.1% with each recording's own names, and to 94.0% when those
  names are mixed with eight names from other recordings. On FLEURS English, own names take it
  from 79.4% to 83.8%. With each recording's own names, the word error rate drops by 0.39 points on
  Spanish and 0.48 on English, and neither drop is chance.
- **Whisper doesn't write names that nobody said.** No transcript in any test set has a name from
  the list that the reference lacks. A list of names that the audio doesn't contain leaves the
  error rate of FLEURS unchanged.
- **Names that the audio lacks can hurt short recordings a little.** On five-second Argentinian
  recordings, where the lists held almost only absent names, the error rate rises by 0.26 points,
  which could be chance, and three names that the speakers said come out misspelled, such as
  "vera cruz" for "Veracruz". The skill tells Claude to pass only names likely to be in the audio.
- **The prompt has to reach every window.** On recordings of about five minutes, Whisper keeps
  reading a prompt passed only at the start for its first windows. After two minutes, names come
  out as they do without hints: 80.3% against 79.6%. Passed to every window, a prompt of up to 120
  characters raises them to 86.7%, and the error rate drops by 1.0 to 1.6 points on each Spanish
  set.
- **Longer prompts make long recordings fail more often.** A large failure, such as a phrase
  repeated for hundreds of words, hits 1 of 48 long recordings with prompts of up to 120
  characters, 3 with up to 200 characters, and 2 with the full lists of up to 452 characters. With
  up to 120 characters, none of the 36 Spanish recordings fails. The counts are small, so the
  difference between the limits isn't certain, but the failures are severe enough to set the
  limit at 120 characters, about eight names.
- **A sample of the dialect doesn't help.** A prompt of Rioplatense chat written with voseo keeps
  86 of 125 voseo forms against 93 without a prompt, and on recordings without speech it makes
  Whisper write fragments of the prompt in 17 of 20 recordings. The server takes names only.
- **Speed doesn't change.** Every run of FLEURS Spanish takes 2.7 to 3.0 seconds per minute of
  audio, with or without hints.

## What was compared

| | |
|---|---|
| Model | `large-v3-turbo-q8_0`, the server default |
| Runtime | Whisper.net 1.9.1 (whisper.cpp) on the Vulkan backend of an NVIDIA GeForce RTX 3080 |
| Program | `hints-eval`, in `poc/HintsEval`, which transcribes a folder through the server's own decoders, voice activity detection (VAD) rule, and `Transcriber`, with an optional prompt per recording |
| Scorer | `poc/scripts/hints_eval.py`, which builds the hint lists, runs `hints-eval`, and scores the transcripts |

The test sets are the ones from the [Gemma 4 evaluation](GEMMA-RESULTS.md#test-sets): 300
recordings of FLEURS Spanish (`es_419`), 150 of FLEURS English (`en_us`), 300 of Google's
Argentinian Spanish corpus (OpenSLR 61), and 150 recordings of that corpus whose sentence has a
voseo verb. Each run gets the language of its test set.

The names of a recording are the words of its reference that start with a capital letter and don't
start a sentence, with consecutive ones joined, such as "Hospital James Paget". The lists are
therefore the names a caller would pass if it knew everything the recording mentions. The
Argentinian references write most names in lowercase, so that corpus has few names of its own.

Each test runs these setups:

| Setup | Prompt |
|-------|--------|
| `none` | No prompt, as the server runs without hints |
| `own` | The recording's own names |
| `distractors` | Eight names from other recordings that this recording doesn't contain |
| `own+distractors` | The recording's own names and its eight distractors, shuffled |
| `voseo` | "Che, ¿vos sabés a qué hora venís mañana? Avisame cuando puedas y decime qué querés hacer, así lo charlamos." |

A name counts as spelled right when the transcript has it as many times as the reference, after
lowercasing and dropping punctuation. Accents count. The word error rate (WER) is measured as in
the Gemma 4 evaluation, and the interval after each difference is the 95% interval from resampling
the recordings: a difference whose interval doesn't include zero isn't chance. Runs are
deterministic: a second run of the same setup gives the same results.

## Short recordings

### Word error rate

| Test set | `none` | `distractors` | `own` | `own+distractors` |
|----------|--------|---------------|-------|-------------------|
| FLEURS Spanish | 2.83% | 2.80% (−0.03, −0.17 to +0.12) | 2.44% (−0.39, −0.59 to −0.21) | 2.36% (−0.47, −0.73 to −0.22) |
| FLEURS English | 4.85% | 4.88% (+0.03, −0.33 to +0.38) | 4.37% (−0.48, −0.91 to −0.06) | 4.47% (−0.38, −0.86 to +0.09) |
| Argentinian Spanish | 3.35% | | | 3.60% (+0.26, −0.08 to +0.63) |
| Argentinian voseo | 4.95% | | | 5.15% (+0.20, −0.21 to +0.66) |

### Names

"Fixed" counts the names that `none` misspells and the setup spells right, and "broken" the
reverse.

| Test set | Names | `none` | `distractors` | `own` | `own+distractors` |
|----------|-------|--------|---------------|-------|-------------------|
| FLEURS Spanish | 284 | 80.6% | 81.0% (3 fixed, 2 broken) | 90.1% (26 fixed, 2 broken) | 94.0% (36 fixed, 1 broken) |
| FLEURS English | 160 | 79.4% | 80.0% (2 fixed, 1 broken) | 83.8% (12 fixed, 5 broken) | 84.4% (13 fixed, 5 broken) |
| Argentinian Spanish | 69 | 89.9% | | | 85.5% (0 fixed, 3 broken) |
| Argentinian voseo | 20 | 90.0% | | | 90.0% (0 fixed, 0 broken) |

The names that hints fix are the ones Whisper spells by ear, such as "Oravec", "Yakarta", "Dunlap",
"Ed Davey MP", and "Sahel". No setup writes a distractor that the recording doesn't contain. On the
Argentinian recordings, which last about five seconds, the prompt is long next to the speech:
"pijama" becomes "villama" with the names and with the voseo prompt alike, and two names from the
recordings' own lists come out split, "cristina agui lera" and "vera cruz".

### The dialect prompt

| Test set | Figure | `none` | `own+distractors` | `voseo` |
|----------|--------|--------|-------------------|---------|
| Argentinian voseo | Voseo forms kept, of 125 | 93 | 89 | 86 |
| Argentinian voseo | WER | 4.95% | 5.15% | 5.36% (+0.41, −0.14 to +0.98) |
| Argentinian Spanish | Voseo forms kept, of 29 | 23 | 22 | 19 |
| Argentinian Spanish | WER | 3.35% | 3.60% | 3.64% (+0.29, −0.04 to +0.63) |

With the voseo prompt, Whisper drops the accent of some forms, such as "podes" for "podés". The
prompt that lets Gemma 4 keep voseo (see [the dialect prompt](GEMMA-RESULTS.md#the-dialect-prompt))
is an instruction; Whisper doesn't follow instructions, and a sample of the dialect doesn't steer
it toward voseo.

### Recordings without speech

Twenty recordings of 10 seconds of white noise, brown noise, hum, and room tone, transcribed in
Spanish. The server's rule turns VAD off for all of them, because their level barely varies.

| Setup | Recordings with text | What Whisper writes |
|-------|----------------------|---------------------|
| `none` | 9 of 20 | "Gracias." |
| Six names as hints | 0 of 20 | Only punctuation |
| `voseo` | 17 of 20 | Fragments of the prompt, such as "¿Vale?" and "Así que, ¿vos sabés?" |

## Long recordings

Whisper decodes 30-second windows and reads the prompt with the text of the previous windows. A
prompt passed only at the start leaves Whisper's context once the transcript fills it, after one or
two windows. Passed to every window ("carried"), it takes part of the context in each one.

### Test sets

The scorer joins the recordings of a set, with half a second of silence between them, into files
of 25 recordings each, and gives each file all of its names as hints. Shuffling the recordings
first makes other files from the same set.

| Long set | Files | Length of each file | Full prompt |
|----------|-------|---------------------|-------------|
| Spanish A, B, and C (FLEURS Spanish in three orders) | 36 | 4.7 to 6.1 min | 114 to 428 characters |
| English A and B (FLEURS English in two orders) | 12 | 3.7 to 4.5 min | 168 to 452 characters |

The setups carry the full lists, or as many of their names as fit in 200 or 120 characters, picked
across the file, or pass the full lists only at the start.

### Word error rate

| Long set | `none` | Carried, 120 | Carried, 200 | Carried, full | Start only, full |
|----------|--------|--------------|--------------|---------------|------------------|
| Spanish A | 3.91% | 2.84% (−1.06, −2.28 to −0.17) | 3.23% (−0.67, −2.27 to +1.15) | 3.52% (−0.39, −2.29 to +2.16) | 3.91% (0.00, −1.68 to +1.68) |
| Spanish B | 3.65% | 2.65% (−1.00, −2.02 to −0.22) | 2.39% (−1.26, −2.31 to −0.48) | 2.45% (−1.19, −2.25 to −0.39) | 2.92% (−0.73, −1.74 to −0.01) |
| Spanish C | 4.35% | 2.75% (−1.60, −2.47 to −0.77) | 2.54% (−1.80, −2.73 to −0.93) | 2.49% (−1.86, −2.78 to −1.00) | 3.66% (−0.69, −1.55 to +0.04) |
| English A | 19.30% | 22.93% (+3.64, −0.81 to +8.88) | 18.69% (−0.61, −3.15 to +3.31) | 22.04% (+2.74, −1.08 to +7.01) | 22.74% (+3.44, −0.66 to +8.65) |
| English B | 24.27% | 17.19% (−7.08, −20.48 to +1.09) | 36.94% (+12.66, −12.64 to +42.06) | 28.80% (+4.53, −17.77 to +33.61) | 18.76% (−5.52, −18.42 to +1.40) |

The English files fail without hints too: Whisper writes 2,817 and 2,947 words of the 3,135 in the
references, skipping whole sentences, while the same recordings one by one have a WER of 4.85%.
They test how hints behave where Whisper is already unstable, and their differences are too wide
to rank the setups.

### Large failures

A large failure at least doubles a file's errors and adds 20 or more.

| Setup | Spanish files | English files | What happens |
|-------|---------------|---------------|--------------|
| Carried, 120 | 0 of 36 | 1 of 12 | Whisper skips passages (63 to 138 errors) |
| Carried, 200 | 1 of 36 | 2 of 12 | Repetition loops, such as "the city is also the most important part of the city of the united states" for hundreds of words (111 to 475 errors) |
| Carried, full | 1 of 36 | 1 of 12 | A sentence that nobody says, then a loop on "la ciudad de México" (23 to 96 errors), and the same English loop |
| Start only, full | 2 of 36 | 0 of 12 | Whisper skips a sentence of 26 to 40 words |

Smaller loops, which don't double the errors, also appear in English A both with the full lists
carried and with the full lists passed only at the start.

### Names

| Setup | Spanish names, of 870 | Spanish names after 2 minutes, of 427 | English names, of 329 |
|-------|-----------------------|----------------------------------------|-----------------------|
| `none` | 78.9% | 79.6% | 66.3% |
| Carried, 120 | 86.9% | 86.7% | 67.2% |
| Carried, 200 | 90.6% | 89.9% | 66.3% |
| Carried, full | 91.7% | 90.6% | 68.4% |
| Start only, full | 83.3% | 80.3% | 66.6% |

Prompts of 120 characters hold fewer names than the full lists, so they fix fewer of them.

## Limits

- The recordings are sentences read aloud. No test set has conversations or meetings with known
  names, and the long files change speaker and topic every 12 seconds or so, which real recordings
  don't.
- The hint lists come from the references, so they hold every name a recording mentions. A caller
  that knows only some of the names gets part of the gain.
- Large failures are rare, and 48 long files show their rate only roughly.
- Only the default model on one GPU was measured.

## Reproduce

The test sets come from `poc/scripts/fetch_testsets.py` on the
[`poc/gemma-4-evaluation` branch](https://github.com/OrihuelaConde/local-whisper-mcp/tree/poc/gemma-4-evaluation/poc/scripts).
To build the program, run the setups on a test set, and score them, run the following commands from
the repository root:

```powershell
dotnet build poc/HintsEval -c Release
python poc/scripts/hints_eval.py prepare SET
python poc/scripts/hints_eval.py run --exe EXE --set SET --setup none --language LANGUAGE
python poc/scripts/hints_eval.py run --exe EXE --set SET --setup own --prompts SET/hints/own.tsv --carry --language LANGUAGE
python poc/scripts/hints_eval.py score --set SET
```

Replace the following:

- `SET`: a test-set folder that git ignores, such as `test-audio/datasets/fleurs-es`, with
  `audio/*.wav` and `refs.tsv`.
- `EXE`: the absolute path of `poc/HintsEval/bin/Release/net10.0/hints-eval.exe`.
- `LANGUAGE`: the language of the test set, such as `es` or `en`.

The other setups use `SET/hints/distractors.tsv`, `SET/hints/own+distractors.tsv`, or `--voseo`.
To build long files from a test set, run `python poc/scripts/hints_eval.py long SET LONG_SET`,
optionally with `--seed N`, and run the setups on `LONG_SET` with `LONG_SET/hints/own-120.tsv`,
`own-200.tsv`, or `own.tsv`. To build recordings without speech, run
`python poc/scripts/hints_eval.py noise NOISE_SET`, and pass `NOISE_SET/hints/distractors.tsv` as
the hints.
