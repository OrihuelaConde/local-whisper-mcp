# Gemma 4 as an alternative transcription engine

This page records a proof of concept that transcribes audio with Gemma 4 instead of Whisper and
compares the two on the same files. Gemma 4 runs through
[LiteRtLmSharp](https://github.com/OrihuelaConde/LiteRtLmSharp), the .NET binding for Google's
LiteRT-LM. Whisper stays the server's engine; the question is whether Gemma 4 earns a place next to
it as an option.

## Verdict

Gemma 4 isn't better than Whisper overall, and it's better in two cases that matter.

- **On a GPU, Whisper wins.** It's 1.7 to 3.6 times faster, it keeps the speaker's exact words,
  and it gives timestamps. Gemma 4 gives no timestamps, so it can't produce the `srt` and `json`
  formats.
- **On a CPU, Gemma 4 E2B wins on speed.** It transcribes a voice note 2.8 times faster than
  `large-v3-turbo-q8_0` and keeps a third of the cores busy, with about the same memory.
- **On a quiet, noisy lecture, the result is mixed.** Gemma 4 transcribes passages that Whisper
  skips, and it gets some technical terms right that Whisper gets wrong. It also falls into
  repetition loops that need two guards to stop.
- **Gemma 4 rewrites dialect.** With the prompt from its model card, it turns Rioplatense voseo
  into tuteo. A prompt that names the dialect fixes most of it, which also shows the model's main
  advantage: instructions steer it.

As an optional engine, Gemma 4 E2B fits users without a GPU who want plain text. The work it takes
is in [What an optional Gemma engine takes](#what-an-optional-gemma-engine-takes).

## What was compared

| | Whisper | Gemma 4 |
|---|---------|---------|
| Model | `large-v3-turbo-q8_0`, the server default | `gemma-4-E2B-it` and `gemma-4-E4B-it`, in the `.litertlm` format |
| Runtime | Whisper.net 1.9.1 (whisper.cpp) | LiteRT-LM v0.18.0 through LiteRtLmSharp at commit `4a115dc` |
| GPU backend | Vulkan | WebGPU over Direct3D 12 for the language model; the audio encoder runs on the CPU |
| Model file | 834 MiB | 2.4 GiB (E2B) and 3.4 GiB (E4B) |

Both engines run in `poc/GemmaAsr`, one engine per process, on the samples that the server's own
decoders produce. Whisper runs through the server's `Transcriber`, with the server's rule for voice
activity detection (VAD). Gemma 4 gets the audio as consecutive clips of up to 30 seconds, cut at
the quietest moment near the end of each clip, each one in a new conversation with the speech
recognition prompt from the [Gemma 4 model card](https://huggingface.co/google/gemma-4-E2B-it) and
greedy decoding. Both engines detect the language unless a table says otherwise.

The test machine is the one in [RESULTS.md](RESULTS.md#test-machine). Whisper uses 8 CPU threads,
the server default, and LiteRT-LM chooses its own thread count. Each run is a new process with the
model caches already built, so the first clip of each run includes the GPU warm-up.

## Speed and memory

### A 24-second voice note

`es-short.wav` is a synthetic Spanish note. "Cores busy" is processor time over elapsed time,
measured on the CPU runs.

| Engine | Device | Load | Transcribe | Seconds per audio minute | Cores busy | Peak RAM | VRAM |
|--------|--------|------|------------|--------------------------|------------|----------|------|
| Whisper | GPU | 0.9 s | 1.0 s | 2.4 | - | 0.9 GiB | 1.3 GiB |
| Gemma 4 E2B | GPU | 1.1 s | 2.8 s | 6.8 | - | 0.9 GiB | 1.6 GiB |
| Gemma 4 E4B | GPU | 2.1 s | 3.6 s | 8.9 | - | 1.1 GiB | 3.4 GiB |
| Whisper | CPU | 0.6 s | 13.2 s | 32.7 | 7.6 | 1.3 GiB | - |
| Gemma 4 E2B | CPU | 0.2 s | 4.7 s | 11.7 | 2.5 | 1.4 GiB | - |
| Gemma 4 E4B | CPU | 0.3 s | 8.9 s | 21.9 | 3.1 | 2.9 GiB | - |

A real WhatsApp voice note of the same length gives the same order: 1.0 s, 3.6 s, and 5.0 s on the
GPU, and 13.2 s, 6.3 s, and 11.2 s on the CPU.

### A long recording

The 85-minute lecture from [RESULTS.md](RESULTS.md#findings) is quiet and noisy. Gemma 4 runs with
the language named and the repetition limit described in [Findings](#findings). The CPU rows name
the language for Whisper too.

| Engine | Device | Audio | Transcribe | Seconds per audio minute | Peak RAM | VRAM |
|--------|--------|-------|------------|--------------------------|----------|------|
| Whisper | GPU | 85 min | 141 s | 1.65 | 2.6 GiB | 2.9 GiB |
| Gemma 4 E2B | GPU | 85 min | 236 s | 2.77 | 3.4 GiB | 1.6 GiB |
| Gemma 4 E4B | GPU | 85 min | 312 s | 3.66 | 3.6 GiB | 3.4 GiB |
| Whisper | CPU | First 10 min | 261 s | 26.1 | 2.5 GiB | - |
| Gemma 4 E2B | CPU | First 10 min | 99 s | 9.9 | 2.6 GiB | - |
| Gemma 4 E4B | CPU | First 10 min | 184 s | 18.4 | 4.1 GiB | - |

On the GPU, a warm Gemma 4 E2B takes about 1.3 s per 30-second clip: 0.1 s to read the prompt and
the audio tokens, 0.7 s to write the answer at 115 tokens per second, and the remaining 0.5 s to
prepare the clip, which includes the audio encoder on the CPU.

### Disk

LiteRT-LM writes compiled caches next to the model file on the first load, and the first load takes
longer: 3.9 s for E2B and 13.5 s for E4B on the GPU.

| Model | Model file | GPU caches | CPU caches |
|-------|------------|------------|------------|
| Whisper `large-v3-turbo-q8_0` | 834 MiB | None | None |
| Gemma 4 E2B | 2.4 GiB | 0.8 GiB | 0.8 GiB |
| Gemma 4 E4B | 3.4 GiB | 2.2 GiB | 2.2 GiB |

Each cache figure includes the caches of the audio encoder, about 0.1 GiB, which both devices
share.

## Accuracy

### Synthetic samples with a known text

The samples speak a 57-word note with a text-to-speech voice. The note ends with voseo
("confirmame si podés llegar a tiempo"). The figures are word error rates against the text, after
lowercasing and dropping punctuation.

| Sample | Whisper | Gemma 4 E2B | Gemma 4 E4B |
|--------|---------|-------------|-------------|
| `es-short.wav`, clean | 0.0% | 3.5% | 3.5% |
| `es-noisy.wav`, loud pink noise | 0.0% | 3.5% | 3.5% |
| `es-pauses.wav`, 25 s of silence and 30 s of faint noise | 1.8% | 1.8% | 1.8% |
| `es-pauses-noise.wav`, the same over brown noise | 1.8% | 0.0% | 1.8% |
| `es-long.wav`, the note seven times in a row | 11.8% | 4.3% | 4.3% |

Every error in the first four samples is one of the two voseo words. Gemma 4 writes "confírmame si
puedes" on the clean sample; Whisper writes "puedes" or "podes" on the samples with pauses. No
engine invents text in the silence or the noise. On `es-long.wav`, Whisper drops and repeats whole
sentences, with VAD and without it (7.5%); a recording that repeats itself isn't a realistic case.

With a prompt that asks for Argentinian Spanish and for the speaker's voseo forms, both Gemma 4
models transcribe `es-short.wav` without errors.

### Real voice notes

The two WhatsApp notes have no reference text, so the comparison is between engines. Whisper's
transcript of the 24-second note reads as correct Rioplatense Spanish.

- **Fillers.** Gemma 4 keeps fillers such as "eh"; Whisper drops them. On the 4-second note, one
  filler is the only difference between the engines.
- **Voseo.** On the 24-second note, both Gemma 4 models turn one or two voseo verbs into tuteo,
  and both replace one voseo verb phrase with a different verb, which changes the meaning.
- **The dialect prompt.** With it, E4B recovers that verb phrase and the voseo forms: 7 word
  differences from Whisper's 79-word transcript remain, 4 of them fillers. E2B recovers one verb and
  still gets the verb phrase wrong.

### The lecture

The lecture has no reference text either. The table counts the words of each transcript and the
share of them inside repetition loops, which are a unit of one to six words repeated four or more
times in a row.

| Engine | Setup | Words | In loops |
|--------|-------|-------|----------|
| Whisper | Server defaults | 9,038 | 2.9% |
| Gemma 4 E2B | Model card prompt | 12,104 | 24.8% |
| Gemma 4 E4B | Model card prompt | 10,473 | 12.4% |
| Gemma 4 E2B | Language named | 11,733 | 18.4% |
| Gemma 4 E4B | Language named | 11,116 | 17.5% |
| Gemma 4 E2B | Language named, repetition limit | 9,375 | 0.5% |
| Gemma 4 E4B | Language named, repetition limit | 9,326 | 0.2% |

- **Loops.** With the model card prompt, 10 of the 186 clips of E2B and 5 of E4B repeat one word
  until they reach the 512-token output limit. Some of those loops are in Italian, Korean, or
  Japanese.
- **Skipped speech.** In the first 10 minutes, with the language named for every engine, Whisper
  writes 735 words on the GPU and 820 on the CPU, E2B 1,152, and E4B 1,183. The two Gemma 4
  transcripts share sentences that Whisper's doesn't have, so Whisper skips speech there. Over the whole lecture, the three transcripts have about the same number of words outside
  loops.
- **Disagreement.** With both guards on, 44% to 47% of the words differ between each Gemma 4
  transcript and Whisper's, against 15% to 19% on the 24-second voice note. Without a reference
  text, the figure doesn't say which engine is right.
- **Terms.** Gemma 4 writes a database term correctly where Whisper writes words that sound like
  it. Whisper keeps the lecturer's voseo, and an English term that Gemma 4 turns into Spanish
  words.

## Findings

- **Clips of 30 seconds.** The model card sets the limit. Audio takes 25 tokens per second, so a
  30-second clip is about 750 tokens. LiteRT-LM also accepts a 60-second clip and transcribes it
  correctly, which the model card doesn't promise.
- **No timestamps.** The answer is plain text. The only times known are the start and end of each
  clip.
- **Short tails.** A leftover clip of 0.46 s makes the model answer "3". The splitter moves the
  last cut to the middle of what is left, so no clip is that short.
- **Repetition loops.** Whisper guards against loops with its compression-ratio check and retries;
  a chat model has no such guard. Naming the language in the prompt removes the loops in other
  languages, and LiteRT-LM's limit on repeated sequences of six tokens removes the rest. What the
  model writes after the limit stops a loop is still text about noise, not speech.
- **Steering by prompt.** The prompt decides the dialect, the number format, and whether fillers
  stay. A server for a global audience can't know the dialect, so the caller would have to pass it.
- **Samplers on the CPU.** On the CPU backend, LiteRT-LM v0.18.0 implements only the top-p sampler
  type. The greedy and top-k types fail with "Sampler type not implemented yet". Top-p with a top-k
  cut of 1 gives greedy decoding on both backends.
- **The audio encoder runs on the CPU.** The Gemma 4 model files declare the CPU as the only
  backend of their audio encoder, on every platform.
- **Downloads.** The model files are on Hugging Face under the Apache 2.0 license, and they
  download without an account.
- **Platforms.** LiteRtLmSharp publishes native packages for `win-x64`, `linux-x64`, and
  `osx-arm64`, and has a `linux-arm64` library. It has none for `osx-x64`, which the server
  supports.
- **Native library size.** The LiteRT-LM library is 48 MiB on `win-x64`, plus 19 MiB for the
  Direct3D shader compiler, 126 MiB on `linux-x64`, and 63 MiB on `osx-arm64`.
- **Native AOT.** Published with Native AOT for `win-x64`, one 3.7 MiB executable transcribes with
  Gemma 4 on the GPU and on the CPU, and with Whisper on Vulkan. LiteRtLmSharp adds no trim or AOT
  warnings.
- **Releasing the model.** After the program disposes the engine, GPU memory in use returns to
  within 20 MiB of the reading before the load, from 1.6 GiB (E2B) and 3.4 GiB (E4B) above it, and
  the process shrinks to under 200 MiB of RAM. LiteRT-LM allows one chat engine at a time per
  process.

## What an optional Gemma engine takes

Gemma 4 as a second engine in the server needs the following, none of which this proof of concept
builds:

- A way to choose the engine, and a model download of 2.4 GiB or more with its own progress
  messages.
- The clip splitter and the two loop guards from this proof of concept, plus a check that catches
  an answer that is still a loop.
- A decision for `srt` and `json`: refuse them, or return one segment per clip.
- A `language` that reaches the prompt, and a way for the caller to pass a dialect or vocabulary.
- A host that loads one engine at a time and disposes the Gemma 4 engine after the idle timeout,
  as it does with Whisper.
- A build per platform that leaves the engine out where LiteRT-LM has no library, and a
  LiteRtLmSharp release that wraps the LiteRT-LM version measured here.

## Reproduce

The proof of concept needs a checkout of LiteRtLmSharp next to this repository, with its native
libraries restored, and the model files in `models/gemma`.

To build the program and run the comparison, run the following commands from the repository root:

```powershell
dotnet build poc/GemmaAsr -c Release
python poc/scripts/compare_asr.py run --exe EXE --out OUT --audio AUDIO --setup whisper-gpu gemma-e2b-gpu gemma-e4b-gpu
python poc/scripts/compare_asr.py report --out OUT
```

Replace the following:

- `EXE`: the absolute path of `poc/GemmaAsr/bin/Release/net10.0/gemma-asr.exe`.
- `OUT`: a folder that git ignores, such as `test-audio/gemma-poc`. The script writes the
  transcripts there.
- `AUDIO`: one or more audio files.

To name the language and limit repetitions, add `--language es -- --no-repeat-ngram 6`. To compare
against a known text, save it as `OUT/refs/AUDIO_FILE_NAME.txt`.
