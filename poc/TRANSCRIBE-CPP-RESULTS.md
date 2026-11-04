# Parakeet-ultra and Cohere Transcribe against Whisper

This page records a proof of concept that compares two models running on
[transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) with the server's Whisper model,
on the same files and with the same scoring as the [Gemma 4 evaluation](GEMMA-RESULTS.md).

## Verdict

Parakeet-ultra is much faster than Whisper and about as accurate, except on Argentinian Spanish,
where Whisper keeps a small lead. Cohere Transcribe doesn't earn a place.

- **Parakeet-ultra accuracy.** It ties with Whisper on FLEURS Spanish, FLEURS English, and the
  voseo sentences: no difference has an interval that excludes zero. On the general Argentinian set
  it makes more errors: 4.0% against 3.3%, a difference of 0.7 points (0.2 to 1.2).
- **Parakeet-ultra speed.** On the GPU it's 3.0 to 5.9 times faster than Whisper over the test
  sets. On the CPU it transcribes a 24-second note 6.9 times faster, and 5-second recordings 38
  times faster than `large-v3-turbo-q8_0`.
- **Parakeet-ultra on a CPU.** It beats Whisper `small` on both speed and accuracy: 11.5 times
  faster, with 4.3% errors against 7.8%. It's also faster than Whisper `base`, which makes 16.9%.
- **Memory.** Parakeet-ultra uses about the same VRAM and RAM as Whisper. Cohere Transcribe uses
  twice the VRAM and, on the CPU, three times the RAM.
- **Cohere Transcribe.** It's worse than Whisper on both Argentinian sets, loses about half of the
  voseo forms, and gives no timestamps, so it can't produce `srt` output.

- **Long audio.** With a guard against repetition loops, Parakeet-ultra makes fewer errors than
  Whisper on every set of 5-minute files. It's also 1.4 to 2.4 times faster, and it skips far less
  speech: 100 words in 48 files, where Whisper skips 586.
- **Noise.** Parakeet-ultra ties with Whisper at 20 dB and beats it at 10, 5, and 0 dB. On
  recordings without speech it writes nothing, where Whisper writes "Gracias." on 9 of 20.
- **Repetition loops.** Without the guard, Parakeet-ultra gets stuck on one file of 48 and on one
  noisy recording of 1,200, and it writes more than 1,000 extra words each time. The library
  doesn't report it. A guard of about 30 lines in the caller repairs both cases.
- **Native AOT.** A .NET 10 Native AOT program runs transcribe.cpp through its C API, with the DLLs
  from the Python wheel, and writes the same bytes as the Python binding. The native files take
  about as much space as Whisper.net's.

Parakeet-ultra has limits that Whisper doesn't: it covers 25 languages, takes no prompt or
vocabulary, and has a CC-BY-4.0 license. The language hint has no effect on its transcripts. It
also needs more VRAM on long files: 2.3 to 2.8 GiB against 1.4 to 1.9 GiB.

## What was compared

| | Whisper | Parakeet-ultra | Cohere Transcribe |
|---|---------|----------------|-------------------|
| Model | `large-v3-turbo-q8_0`, the server default | `parakeet-ultra-Q8_0.gguf` | `cohere-transcribe-03-2026-Q8_0.gguf` |
| Runtime | Whisper.net (whisper.cpp), through the server's `Transcriber` | `transcribe-cpp` 0.4.1, the Python binding | Same as Parakeet-ultra |
| Model file | 834 MiB | 740 MB | 2.41 GB |
| Timestamps | Segments | Tokens | None |
| Prompt or vocabulary | Yes | No | No |
| Languages | 99 | 25 | 14 |
| License | MIT | CC-BY-4.0 | Apache-2.0 |

The test sets, the scoring, and the voseo count are the ones in
[GEMMA-RESULTS.md](GEMMA-RESULTS.md#accuracy-against-reference-text): `compare_asr.py score`, where
accents count and the intervals come from resampling the recordings. Each engine gets the language
of the test set. All engines get the same samples, because
[`tcpp_transcribe.py`](scripts/tcpp_transcribe.py) resamples with the same filter as the server.

The test machine runs Windows 11 with an NVIDIA GeForce RTX 3080 (10 GB, driver 616.92) and an
Intel Core i7-12700F, the processor that Windows reports. All engines use 8 CPU threads and Vulkan
on the GPU. Each engine ran on this machine in the same session, and Whisper's transcripts match the
stored Gemma baseline word for word. On 5-second recordings on the CPU, Whisper takes 1.7 to 2.0
times as long as the figures in GEMMA-RESULTS.md, which name a different processor, so compare the
CPU figures only within this page.

## Accuracy

The interval after each rate is the 95% interval from resampling the recordings.

| Test set | Whisper | Parakeet-ultra | Cohere Transcribe |
|----------|---------|----------------|-------------------|
| FLEURS Spanish | 2.8% (2.3 to 3.4) | 3.0% (2.4 to 3.6) | 3.3% (2.7 to 4.1) |
| FLEURS English | 4.8% (4.0 to 5.8) | 4.9% (4.1 to 5.8) | 4.6% (3.8 to 5.5) |
| Argentinian Spanish | 3.3% (2.6 to 4.1) | 4.0% (3.2 to 5.0) | 4.5% (3.6 to 5.6) |
| Argentinian voseo | 4.9% (3.9 to 6.2) | 5.0% (3.9 to 6.2) | 7.8% (6.6 to 9.1) |

The next table is the difference from Whisper in percentage points, with its own 95% interval.

| Test set | Parakeet-ultra | Cohere Transcribe |
|----------|----------------|-------------------|
| FLEURS Spanish | +0.2 (-0.2 to +0.5) | +0.5 (-0.1 to +1.2) |
| FLEURS English | +0.1 (-0.6 to +0.8) | -0.3 (-1.2 to +0.6) |
| Argentinian Spanish | +0.7 (+0.2 to +1.2) | +1.2 (+0.5 to +2.0) |
| Argentinian voseo | +0.1 (-0.8 to +0.9) | +2.8 (+1.9 to +3.8) |

The next table counts the voseo forms of the references that each transcript keeps.

| Test set | Voseo forms | Whisper | Parakeet-ultra | Cohere Transcribe |
|----------|-------------|---------|----------------|-------------------|
| Argentinian Spanish | 29 | 23 | 22 | 13 |
| Argentinian voseo | 125 | 93 | 90 | 61 |

On the 150 voseo recordings, Cohere Transcribe writes "puedes" for "podés" 24 times, Parakeet-ultra
11, and Whisper 8. No transcript of these test sets has a repetition loop, and no run failed or was
truncated.

## Speed and memory

### The test sets

With the model loaded once, on the GPU. Transcribe time excludes reading the audio. Whisper's
includes voice activity detection (VAD) under the server's rule.

| Test set | Audio | Whisper | Parakeet-ultra | Cohere Transcribe |
|----------|-------|---------|----------------|-------------------|
| FLEURS Spanish, 12 s per recording | 61 min | 122 s | 40 s | 87 s |
| FLEURS English, 9 s per recording | 24 min | 59 s | 17 s | 42 s |
| Argentinian Spanish, 5 s per recording | 25 min | 135 s | 23 s | 77 s |
| Argentinian voseo, 5 s per recording | 13 min | 68 s | 16 s | 35 s |

Peak dedicated VRAM per process, over the same runs, sampled about once per second:

| Test set | Whisper | Parakeet-ultra | Cohere Transcribe |
|----------|---------|----------------|-------------------|
| FLEURS Spanish | 1.8 GiB | 1.8 GiB | 4.0 GiB |
| FLEURS English | 1.8 GiB | 1.8 GiB | 4.0 GiB |
| Argentinian Spanish | 1.8 GiB | 1.8 GiB | 3.8 GiB |
| Argentinian voseo | 1.8 GiB | 1.8 GiB | 3.9 GiB |

These readings use the `GPU Process Memory` performance counter, because the whole-GPU reading from
`nvidia-smi` moved by more than 1 GiB between runs with other applications open.

### A 24-second voice note

`es-short.wav` is a synthetic Spanish note. Each run is a new process, and the figures are the
median of the runs after the first. All engines transcribe it with no word errors.

| Engine | Device | Load | Transcribe | Peak RAM | VRAM |
|--------|--------|------|------------|----------|------|
| Whisper | GPU | 1.1 s | 0.58 s | 0.9 GiB | 1.3 GiB |
| Parakeet-ultra | GPU | 0.8 s | 0.24 s | 0.8 GiB | 0.8 GiB |
| Cohere Transcribe | GPU | 2.2 s | 0.52 s | 1.1 GiB | 2.6 GiB |
| Whisper | CPU | 0.8 s | 11.8 s | 1.2 GiB | - |
| Parakeet-ultra | CPU | 0.3 s | 1.7 s | 1.2 GiB | - |
| Cohere Transcribe | CPU | 0.7 s | 5.5 s | 3.6 GiB | - |

The VRAM figures on this table come from `nvidia-smi` in runs where its baseline held steady, and
Whisper's from [GEMMA-RESULTS.md](GEMMA-RESULTS.md#a-24-second-voice-note). Peak RAM for
transcribe.cpp includes the Python interpreter.

The first GPU run of each transcribe.cpp model on the machine took 15.2 s for Parakeet-ultra and
7.7 s for Cohere Transcribe, before later runs dropped to the figures above. This cost was not
measured for Whisper.

### Short recordings on the CPU

Every fifth recording of the Argentinian Spanish set, 60 recordings of 5 seconds each, with the
model loaded once and the CPU only. The difference column compares each rate with Whisper `small`.

| Engine | Seconds per audio minute | Word error rate | Difference from `small` | Voseo forms kept, of 5 |
|--------|--------------------------|-----------------|-------------------------|------------------------|
| Parakeet-ultra | 3.4 | 4.3% (2.5 to 6.2) | -3.5 (-5.9 to -1.4) | 4 |
| Whisper `base` | 12.0 | 16.9% (12.5 to 21.9) | +9.1 (+5.3 to +13.4) | 1 |
| Cohere Transcribe | 12.2 | 4.5% (2.5 to 6.8) | -3.3 (-5.5 to -1.4) | 3 |
| Whisper `small` | 38.7 | 7.8% (5.3 to 10.6) | - | 1 |
| Whisper `large-v3-turbo-q8_0` | 127.6 | 2.6% (1.2 to 4.1) | -5.2 (-8.2 to -2.8) | 5 |

Whisper's cost per recording has a floor, because it encodes a 30-second window however short the
audio is. Parakeet-ultra's transcripts are the same word for word on the CPU and on the GPU.

## Long audio

Five sets of files of about 5 minutes, which the hints evaluation built by joining FLEURS recordings
with `hints_eval.py long`: 36 Spanish files and 12 English files, 4.0 hours in all. "Guard" is
Parakeet-ultra with `--loop-guard`, described in [Repetition loops](#repetition-loops). Both engines
run on the GPU, and Whisper gets the server's VAD rule.

| Set | Whisper | Parakeet-ultra | Guard | Difference of the guard run from Whisper |
|-----|---------|----------------|-------|------------------------------------------|
| Spanish A | 3.91% (2.96 to 5.03) | 3.14% (2.40 to 3.82) | 3.14% (2.40 to 3.82) | -0.77 (-2.12 to +0.25) |
| Spanish B | 3.65% (2.77 to 4.71) | 33.23% (2.48 to 97.81) | 2.92% (2.46 to 3.42) | -0.73 (-1.73 to +0.12) |
| Spanish C | 4.35% (3.47 to 5.28) | 3.24% (2.57 to 3.96) | 3.24% (2.57 to 3.96) | -1.10 (-2.17 to -0.04) |
| English A | 19.30% (14.49 to 24.88) | 8.29% (6.24 to 11.26) | 8.29% (6.24 to 11.26) | -11.00 (-17.89 to -4.47) |
| English B | 24.27% (13.25 to 39.10) | 7.62% (5.87 to 9.90) | 7.62% (5.87 to 9.90) | -16.65 (-32.51 to -4.41) |

The next table counts large failures. A file has a large failure when it has at least twice the
errors of the other engine and at least 20 more. Skipped and inserted stretches compare each
transcript with the reference and count those of 15 words or more.

| Set | Large failures: Whisper, Parakeet-ultra | Skipped words: Whisper, Parakeet-ultra | Inserted words: Whisper, Parakeet-ultra |
|-----|-----------------------------------------|----------------------------------------|-----------------------------------------|
| Spanish A | 1, 0 | 39, 0 | 20, 0 |
| Spanish B | 2, 1 | 52, 0 | 0, 2,336 |
| Spanish C | 2, 0 | 90, 0 | 0, 0 |
| English A | 3, 0 | 190, 61 | 0, 0 |
| English B | 4, 0 | 215, 48 | 193, 0 |

Parakeet-ultra's only inserted stretch is the loop that the guard repairs. Whisper's 193 inserted
words on English B include a loop of 27 words.

| Set | Audio | Whisper | Parakeet-ultra | VRAM: Whisper | VRAM: Parakeet-ultra |
|-----|-------|---------|----------------|---------------|----------------------|
| Spanish A | 63 min | 52 s | 38 s | 1.8 GiB | 2.2 GiB |
| Spanish B | 63 min | 76 s | 39 s | 1.8 GiB | 2.2 GiB |
| Spanish C | 63 min | 91 s | 48 s | 1.4 GiB | 2.2 GiB |
| English A | 25 min | 27 s | 12 s | 1.9 GiB | 2.7 GiB |
| English B | 25 min | 22 s | 12 s | 1.8 GiB | 2.2 GiB |

Parakeet-ultra's lead is smaller here than on short recordings because VAD saves Whisper from
encoding the silence between sentences. On the CPU, Parakeet-ultra takes 3.7 s per audio minute on
English A, with a peak of 1.7 GiB of RAM and the same error rate within 0.1 points.

## Noise

Every recording of FLEURS Spanish, with noise mixed in by `tcpp_noise.py`. Each recording gets one
kind of noise: white, pink, or babble, which sums eight stretches of FLEURS English. The noise is
synthetic, so these figures compare the engines and don't predict a real room. The table uses the
guard run; the plain run differs only at 0 dB, on the recording in
[Repetition loops](#repetition-loops).

| Signal-to-noise ratio | Noise | Whisper | Parakeet-ultra | Difference from Whisper | Large failures: Whisper, Parakeet-ultra |
|-----------------------|-------|---------|----------------|-------------------------|-----------------------------------------|
| 20 dB | All | 3.3% (2.7 to 3.9) | 3.2% (2.7 to 3.9) | -0.1 (-0.5 to +0.3) | 0, 0 |
| 10 dB | All | 4.5% (3.7 to 5.2) | 3.8% (3.2 to 4.5) | -0.6 (-1.1 to -0.1) | 0, 0 |
| 5 dB | All | 6.9% (5.8 to 8.2) | 5.0% (4.3 to 5.8) | -1.9 (-3.0 to -1.0) | 1, 0 |
| 0 dB | All | 14.8% (12.6 to 17.1) | 10.2% (8.7 to 11.9) | -4.6 (-6.3 to -3.0) | 2, 0 |
| 0 dB | White | 13.8% (10.8 to 17.1) | 9.8% (7.6 to 12.2) | -4.0 (-5.9 to -2.2) | 0, 0 |
| 0 dB | Pink | 9.2% (7.5 to 10.9) | 6.6% (5.1 to 8.3) | -2.6 (-3.7 to -1.5) | 0, 0 |
| 0 dB | Babble | 21.2% (16.3 to 26.8) | 14.1% (10.9 to 18.1) | -7.1 (-11.5 to -3.0) | 2, 0 |

Each noisy set takes Whisper 127 to 145 s and Parakeet-ultra 35 to 36 s.

On the 20 recordings without speech from `hints_eval.py noise` (white noise, brown noise, hum, and
room tone), Parakeet-ultra writes an empty transcript every time. Whisper writes "Gracias." on 9 and
"..." on the other 11.

## Repetition loops

Parakeet-ultra fell into a loop on `long-s1-03.wav` in Spanish B and on one recording at 0 dB, out of
48 long files and 1,200 noisy recordings. No loop appears in the test sets without added noise. Both
loops repeat "de la Universidad", on the CPU and on the GPU alike, and they turn 18 errors into 2,363
and 8 errors into 1,088.

The cause is in how the decoder spends its symbols. The model file sets
`stt.parakeet.tdt.symbol_budget` to `global`, which allows 10 symbols per encoder frame over the
whole segment instead of per frame. A decoder that keeps predicting a duration of zero stays on one
frame until the budget runs out: 3,112 tokens in 80 ms on the long file. The rest of that segment is
never decoded. `transcribe_run` returns success, and the library's repetition guard doesn't cover the
Parakeet family.

The file's token timestamps make this visible: speech reaches at most 21 tokens in 2 seconds across
the long sets, and the loops reach more than 1,400. With `--loop-guard`, 60 tokens in 2 seconds
triggers a second run of that stretch of audio, cut in two where the decoder got stuck. In every run
of the long and noisy sets, the guard fired twice, on those two recordings, and changed nothing else.
The repaired file has 27 errors against Whisper's 18, and the repaired recording has 6 against 8.

Two cases are a small sample, so the threshold has no margin measured on other loops. A loop slower
than 60 tokens in 2 seconds would pass the guard.

## Native AOT

[`poc/TcppAot`](TcppAot) is a .NET 10 Native AOT console program that calls the C API of
transcribe.cpp through `LibraryImport`. It uses the DLLs of the `transcribe-cpp-native` 0.4.1 wheel
as they are, with no native build. It checks the size of each struct it passes against
`transcribe_abi_struct_size`.

Its transcripts of `es-short.wav` and of one FLEURS recording are byte-identical to those of the
Python binding, on Vulkan and on the CPU.

| Device | Load | Transcribe `es-short.wav` | Peak RAM |
|--------|------|---------------------------|----------|
| Vulkan | 0.9 s | 0.20 s | 0.8 GiB |
| CPU | 0.4 s | 1.16 s | 1.2 GiB |

Load includes 0.2 s to load the backend modules. The first Vulkan run of a new program takes 10.6 s
to transcribe, and 0.2 s from then on. [RESULTS.md](RESULTS.md) records about 7 s for the same first
run with Whisper.

| File | Size | Purpose |
|------|------|---------|
| `tcpp-aot.exe` | 1.5 MiB | The program |
| `transcribe.dll`, `ggml.dll`, `ggml-base.dll` | 2.7 MiB | The library |
| `ggml-vulkan.dll` | 41.6 MiB | Vulkan backend |
| `ggml-cpu-*.dll`, nine files | 9.3 MiB | One CPU backend per instruction set; the library picks the best at run time |

The library's files take 53.6 MiB. `Whisper.net.Runtime.Vulkan` takes 57.2 MiB for `win-x64`. All nine
CPU files matter: with only `ggml-cpu-x64.dll`, `es-short.wav` takes 8 to 10 s on the CPU instead of
1.2 s.

- **Dependencies.** The DLLs need the Visual C++ runtime (`MSVCP140.dll`, `VCRUNTIME140.dll`, and
  `VCRUNTIME140_1.dll`) and `vulkan-1.dll`, as Whisper.net's do.
- **Fallback.** With `ggml-vulkan.dll` removed and the `auto` backend, the program runs on the CPU.
  A machine without a Vulkan driver wasn't tested.
- **Backend loading.** `transcribe_init_backends_default` looks only next to `transcribe.dll`, so the
  files can't be embedded in the executable.

No blocker appeared. The C API is pre-1.0, and the Python binding refuses a library with a different
base version, so a server needs to pin one version and update it with care.

## Findings

- **Language.** With no language, Parakeet-ultra writes the same 300 transcripts on FLEURS Spanish
  as with `es`, and an `en` hint on Spanish audio changes none of 60 transcripts. The binding
  reports an empty detected language in all these runs. Cohere Transcribe has no language detection.
- **Timestamps.** Parakeet-ultra returns token timestamps by default. Cohere Transcribe returns
  none, which rules out the server's `srt` format and the timestamps in its `json` format.
- **Prompts.** Neither model advertises vocabulary, context prompts, or transcript prefixes, so
  neither supports a dialect prompt like the one that helps Gemma 4 keep voseo.
- **Windows wheel.** `transcribe-cpp-native` 0.4.1 ships Vulkan and CPU backends for Windows, and
  both worked with no setup beyond `pip install`.

## What this doesn't measure

- Real recordings with real background noise. The long files join clean recordings, and the noise
  is synthetic.
- Other operating systems. Every figure comes from one Windows machine, including the Native AOT run.
- Whether an unquantized Parakeet-ultra model also loops. Only `Q8_0` was tested.

## Run the measurements

The script and the scorer are in [`poc/scripts`](scripts). The audio sets, models, and transcripts
stay in folders that git ignores. The test sets come from `fetch_testsets.py` on the
[`poc/gemma-4-evaluation` branch](https://github.com/OrihuelaConde/local-whisper-mcp/tree/poc/gemma-4-evaluation/poc/scripts).

To transcribe a set and score it, run the following commands in PowerShell:

```powershell
python -m venv models\transcribe-cpp\venv
models\transcribe-cpp\venv\Scripts\python.exe -m pip install transcribe-cpp==0.4.1 soundfile numpy
models\transcribe-cpp\venv\Scripts\python.exe -I poc\scripts\tcpp_transcribe.py --model MODEL_GGUF --setup SETUP_NAME --out OUT_DIR --audio-dir SET_DIR\audio --language LANGUAGE
models\transcribe-cpp\venv\Scripts\python.exe -I poc\scripts\compare_asr.py score --out OUT_DIR --refs SET_DIR\refs.tsv --baseline whisper-gpu
```

Replace the following:

- `MODEL_GGUF`: the path to a Q8_0 file from
  [parakeet-ultra-gguf](https://huggingface.co/handy-computer/parakeet-ultra-gguf) or
  [cohere-transcribe-03-2026-gguf](https://huggingface.co/handy-computer/cohere-transcribe-03-2026-gguf).
- `SETUP_NAME`: a name for this run.
- `OUT_DIR`: a folder that git ignores, with Whisper's transcripts in
  `OUT_DIR\transcripts\whisper-gpu`.
- `SET_DIR`: the folder of a test set.
- `LANGUAGE`: the language of the test set, or `auto`.
