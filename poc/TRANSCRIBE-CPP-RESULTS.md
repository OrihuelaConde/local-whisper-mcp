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
- **Cohere Transcribe.** It's worse than Whisper on both Argentinian sets, turns about half of the
  voseo into tuteo, and gives no timestamps, so it can't produce `srt` output.

Parakeet-ultra has limits that Whisper doesn't: it covers 25 languages, takes no prompt or
vocabulary, and has a CC-BY-4.0 license. The language hint has no effect on its transcripts.

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
stored Gemma baseline word for word. On the CPU, Whisper takes 1.7 to 1.9 times as long as the
figures in GEMMA-RESULTS.md, which name a different processor, so compare the CPU figures only
within this page.

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
11, and Whisper 8. No transcript has a repetition loop, and no run failed or was truncated.

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

- Long recordings and noisy audio. Every test set here has recordings of 30 seconds or less.
- Running transcribe.cpp from .NET under Native AOT, which is what the server would need. This
  proof of concept uses the Python binding.
- Other operating systems. Every figure comes from one Windows machine.

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
