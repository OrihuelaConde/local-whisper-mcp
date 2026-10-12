# Gemma 4 as an alternative transcription engine

This page records a proof of concept that transcribes audio with Gemma 4 instead of Whisper and
compares the two on the same files. Gemma 4 runs through
[LiteRtLmSharp](https://github.com/OrihuelaConde/LiteRtLmSharp), the .NET binding for Google's
LiteRT-LM. Whisper stays the server's engine; the question is whether Gemma 4 earns a place next to
it as an option.

## Verdict

Gemma 4 isn't more accurate than Whisper, and a larger Gemma 4 model doesn't change that.

- **Whisper is more accurate.** Against human reference text, `large-v3-turbo-q8_0` makes fewer word
  errors than Gemma 4 E2B, E4B, and 12B on every test set: 2.8% against 4.4% to 6.5% on Spanish,
  and 4.8% against 6.2% to 9.6% on English. The differences aren't chance.
- **The largest model isn't the best one.** Gemma 4 12B beats E4B by a little on read sentences
  and loses to it on Argentinian Spanish, because it rewrites more of what it hears. It's also 1.4
  to 2.9 times slower than E4B and takes 7.4 GiB of VRAM.
- **Gemma 4 rewrites dialect, and a prompt fixes it.** With the prompt from its model card, it
  turns most Rioplatense voseo into tuteo. With a prompt that names the dialect, E4B keeps 123 of
  125 voseo forms and makes fewer errors than Whisper on those sentences: 3.4% against 4.9%. That
  is the one accuracy result where Gemma 4 wins, and it needs the caller to know the dialect.
- **On a GPU, Whisper is faster.** It's about twice as fast as E2B on recordings of 10 seconds or
  more, and E2B matches it on 5-second ones.
- **On a CPU, Gemma 4 E2B beats the default Whisper model on speed and ties with Whisper `small`.**
  E2B transcribes a 24-second voice note 2.8 times faster than `large-v3-turbo-q8_0` and a
  5-second one 5.7 times faster, with twice the errors. Whisper `small` runs at about the speed of
  E2B and is as accurate overall: better on English, worse on Argentinian Spanish.
- **Gemma 4 gives no timestamps,** so it can't produce the `srt` and `json` formats, and on noisy
  audio it needs two guards against repetition loops.

The figures don't make the case for a second engine. Users without a GPU get about the same
speed and accuracy from Whisper `small`, which the server already downloads on request and which
gives timestamps. What only Gemma 4 offers is steering by prompt, which pays off when the caller
knows the dialect. Gemma 4 12B doesn't earn a place. The work an optional engine takes is in
[What an optional Gemma engine takes](#what-an-optional-gemma-engine-takes).

## What was compared

| | Whisper | Gemma 4 |
|---|---------|---------|
| Model | `large-v3-turbo-q8_0`, the server default | `gemma-4-E2B-it`, `gemma-4-E4B-it`, and `gemma-4-12B-it`, in the `.litertlm` format |
| Runtime | Whisper.net 1.9.1 (whisper.cpp) | LiteRT-LM v0.18.0 through LiteRtLmSharp at commit `4a115dc` |
| GPU backend | Vulkan | WebGPU over Direct3D 12 for the language model; the audio encoder runs on the CPU |
| Model file | 834 MiB | 2.4 GiB (E2B), 3.4 GiB (E4B), and 6.4 GiB (12B) |

Both engines run in `gemma-asr`, a test program in the `poc/GemmaAsr` folder of the
[`poc/gemma-4-evaluation` branch](https://github.com/OrihuelaConde/local-whisper-mcp/tree/poc/gemma-4-evaluation/poc/GemmaAsr),
one engine per process, on the samples that the server's own decoders produce. Whisper runs through the server's `Transcriber`, with the server's rule for voice
activity detection (VAD). Gemma 4 gets the audio as consecutive clips of up to 30 seconds, cut at
the quietest moment near the end of each clip, each one in a new conversation with the speech
recognition prompt from the [Gemma 4 model card](https://huggingface.co/google/gemma-4-E2B-it) and
greedy decoding.

The test machine is the one in [RESULTS.md](RESULTS.md#test-machine). Whisper uses 8 CPU threads,
the server default, and LiteRT-LM chooses its own thread count. E2B and E4B run with 32-bit
activations on the GPU, the LiteRtLmSharp default, and 12B with 16-bit ones, which it needs (see
[Findings](#findings)).

## Accuracy against reference text

### Test sets

The pages of the Whisper and Gemma 4 models on Hugging Face have demonstration clips without
transcripts. The Gemma 4 model card reports its speech recognition results on FLEURS, a public test
set of sentences read aloud, so the comparison uses FLEURS and a second public corpus with
Argentinian speakers. Every recording comes with the text the speaker read.

| Test set | Recordings | Audio | Reference words | Source |
|----------|------------|-------|-----------------|--------|
| FLEURS Spanish | 300 | 61 min | 7,706 | The first 300 recordings of the `es_419` (Latin American Spanish) test split of [FLEURS](https://huggingface.co/datasets/google/fleurs) |
| FLEURS English | 150 | 24 min | 3,135 | The first 150 recordings of the `en_us` test split of FLEURS |
| Argentinian Spanish | 300 | 25 min | 2,720 | Google's crowdsourced [Argentinian Spanish corpus](https://huggingface.co/datasets/ylacombe/google-argentinian-spanish) (OpenSLR 61), half women and half men |
| Argentinian voseo | 150 | 13 min | 1,475 | Recordings of the same corpus whose sentence has a voseo verb; some are also in the previous set |

Each engine gets the language of the test set, and each setup loads its model once and transcribes
the whole set on the GPU. The word error rate (WER) is the word-level edit distance between the
transcript and the reference, over the reference words, after lowercasing and dropping punctuation.
Accents count, so "sabes" for "sabés" is an error. The references have spellings of their own,
such as "ultimo" without its accent, which count against every engine alike.

### Word error rate

The interval after each rate is the 95% interval from resampling the recordings.

| Test set | Whisper | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 12B |
|----------|---------|-------------|-------------|-------------|
| FLEURS Spanish | 2.8% (2.3 to 3.4) | 6.5% (5.7 to 7.2) | 5.0% (4.3 to 5.6) | 4.4% (3.5 to 5.8) |
| FLEURS English | 4.8% (4.0 to 5.8) | 9.6% (7.5 to 12.4) | 6.5% (5.5 to 7.7) | 6.2% (5.1 to 7.5) |
| Argentinian Spanish | 3.3% (2.6 to 4.1) | 5.8% (4.8 to 6.9) | 4.8% (3.9 to 5.8) | 7.2% (5.8 to 8.8) |
| Argentinian voseo | 4.9% (3.9 to 6.2) | 9.4% (8.0 to 10.9) | 9.2% (7.8 to 10.7) | 13.8% (11.8 to 16.0) |

The next table is the difference from Whisper in percentage points, with its own 95% interval.
No interval includes zero, so Whisper's lead isn't chance on any test set.

| Test set | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 12B |
|----------|-------------|-------------|-------------|
| FLEURS Spanish | +3.6 (+3.0 to +4.3) | +2.2 (+1.6 to +2.7) | +1.6 (+0.7 to +3.0) |
| FLEURS English | +4.7 (+2.8 to +7.4) | +1.7 (+0.6 to +2.7) | +1.3 (+0.2 to +2.7) |
| Argentinian Spanish | +2.5 (+1.6 to +3.4) | +1.5 (+0.8 to +2.2) | +3.9 (+2.5 to +5.4) |
| Argentinian voseo | +4.5 (+3.2 to +5.8) | +4.3 (+3.1 to +5.5) | +8.8 (+6.9 to +10.9) |

The order holds on the recordings without numbers, where digit and word spellings can't differ:
on FLEURS Spanish, 2.0% for Whisper against 5.9%, 4.1%, and 3.6%. No transcript of these test
sets has a repetition loop.

### Voseo

The table counts the voseo forms of the references ("vos", "sos", and verbs such as "podés") that
each transcript keeps.

| Test set | Voseo forms | Whisper | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 12B |
|----------|-------------|---------|-------------|-------------|-------------|
| Argentinian Spanish | 29 | 23 | 11 | 14 | 7 |
| Argentinian voseo | 125 | 93 | 48 | 53 | 24 |

In the 150 voseo recordings, Gemma 4 writes "puedes" for "podés" 36 to 39 times, and Whisper 8. The
larger the Gemma 4 model, the more it rewrites: 12B also changes words that E2B and E4B keep, such
as "tienes alguna preferencia" for "tenés algo en mente".

### The dialect prompt

The same Gemma 4 runs with a prompt that asks for Argentinian Spanish and for the speaker's voseo
forms, against Whisper as it runs in the server:

| Test set | Figure | Whisper | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 12B |
|----------|--------|---------|-------------|-------------|-------------|
| Argentinian voseo | WER | 4.9% | 3.7% | 3.4% | 7.6% |
| Argentinian voseo | Voseo forms kept, of 125 | 93 | 122 | 123 | 119 |
| Argentinian Spanish | WER | 3.3% | 5.9% | 4.8% | 6.9% |
| Argentinian Spanish | Voseo forms kept, of 29 | 23 | 27 | 28 | 28 |

On the voseo sentences, E4B with the dialect prompt beats Whisper by 1.6 points (0.5 to 2.6), and
E2B by 1.2 points (0.0 to 2.4). On the general Argentinian set, the prompt recovers the voseo forms
and leaves the error rate where it was, behind Whisper. The prompt wasn't tested on speakers who
don't use voseo, where it could add voseo that nobody said.

## Speed and memory

### A 24-second voice note

`es-short.wav` is a synthetic Spanish note. Each run is a new process with the model caches already
built, so it includes the GPU warm-up. "Cores busy" is processor time over elapsed time, measured
on the CPU runs.

| Engine | Device | Load | Transcribe | Seconds per audio minute | Cores busy | Peak RAM | VRAM |
|--------|--------|------|------------|--------------------------|------------|----------|------|
| Whisper | GPU | 0.9 s | 1.0 s | 2.4 | - | 0.9 GiB | 1.3 GiB |
| Gemma 4 E2B | GPU | 1.1 s | 2.8 s | 6.8 | - | 0.9 GiB | 1.6 GiB |
| Gemma 4 E4B | GPU | 2.1 s | 3.6 s | 8.9 | - | 1.1 GiB | 3.4 GiB |
| Gemma 4 12B | GPU | 5.5 s | 4.7 s | 11.7 | - | 1.5 GiB | 7.2 GiB |
| Whisper | CPU | 0.6 s | 13.2 s | 32.7 | 7.6 | 1.3 GiB | - |
| Gemma 4 E2B | CPU | 0.2 s | 4.7 s | 11.7 | 2.5 | 1.4 GiB | - |
| Gemma 4 E4B | CPU | 0.3 s | 8.9 s | 21.9 | 3.1 | 2.9 GiB | - |
| Gemma 4 12B | CPU | 7.9 s | 78.7 s | 194 | - | 7.0 GiB | - |

A real WhatsApp voice note of the same length gives the same order: 1.0 s, 3.6 s, 5.0 s, and 5.3 s
on the GPU, and 13.2 s, 6.3 s, and 11.2 s on the CPU for Whisper, E2B, and E4B. On the CPU, 12B
reads 13 tokens per second and writes 3, which rules it out there.

### The test sets

With the model loaded once, as in a server that keeps it in memory, the GPU runs take these
seconds per audio minute:

| Test set | Whisper | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 12B |
|----------|---------|-------------|-------------|-------------|
| FLEURS Spanish, 12 s per recording | 2.4 | 5.4 | 6.3 | 11.1 |
| FLEURS English, 9 s per recording | 3.2 | 6.2 | 7.7 | 11.0 |
| Argentinian Spanish, 5 s per recording | 5.1 | 4.5 | 5.2 | 15.0 |

Whisper's cost per recording has a floor, because it encodes a 30-second window however short the
audio is, so E2B and E4B match it on 5-second recordings. A second run of the same setup differs by
10% to 30% in time and gives the same transcripts, word for word.

### Short recordings on the CPU

Every fifth recording of the Argentinian Spanish set, 60 recordings of 5 seconds each, with the
model loaded once and the CPU only. The last column is the rate on the whole set, from the GPU
runs.

| Engine | Seconds per recording | Seconds per audio minute | Cores busy | Peak RAM | WER on the 60 | WER on the set |
|--------|-----------------------|--------------------------|------------|----------|---------------|----------------|
| Whisper `large-v3-turbo-q8_0` | 6.1 | 74.5 | 7.9 | 1.3 GiB | 2.6% | 3.3% |
| Whisper `small` | 1.6 | 19.3 | 7.9 | 0.8 GiB | 7.8% | 8.2% |
| Whisper `base` | 0.5 | 6.2 | 7.9 | 0.4 GiB | 16.9% | 16.0% |
| Gemma 4 E2B | 1.1 | 13.1 | 2.8 | 1.5 GiB | 5.6% | 5.8% |
| Gemma 4 E4B | 2.6 | 31.3 | 3.2 | 3.0 GiB | 4.6% | 4.8% |

Whisper encodes a 30-second window for any recording, which costs the default model 6 s on the
CPU. Gemma 4's cost follows the length of the audio, so on short voice notes E2B is 5.7 times
faster than the default model, with about twice its errors and a third of the cores. On the
24-second note, `small` takes 3.7 s on the CPU against 4.7 s for E2B.

### Whisper small against Gemma 4 E2B

Whisper `small` is the Whisper model closest to E2B in speed on a CPU, so it decides whether E2B
adds anything there. The last column is the rate of E2B minus the rate of `small`, with its 95%
interval.

| Test set | Whisper `small` | Gemma 4 E2B | Difference |
|----------|-----------------|-------------|------------|
| FLEURS Spanish | 5.7% | 6.5% | +0.7 (0.0 to +1.4) |
| FLEURS English | 6.6% | 9.6% | +3.0 (+1.0 to +5.7) |
| Argentinian Spanish | 8.2% | 5.8% | -2.4 (-3.6 to -1.1) |
| Argentinian voseo | 11.5% | 9.4% | -2.1 (-3.8 to -0.6) |

Neither wins: `small` is better on English, E2B on Argentinian Spanish, and they tie on the read
Spanish of FLEURS. `small` keeps 41 of the 125 voseo forms, fewer than E2B. `small` is a 465 MiB
download against 2.4 GiB, takes half the memory, and gives timestamps; E2B keeps a third of the
cores busy and is faster on recordings of a few seconds. Whisper `base` is too inaccurate to
compete: 11.2% on FLEURS Spanish and 16.0% on Argentinian Spanish.

### A long recording

The 85-minute lecture from [RESULTS.md](RESULTS.md#findings) is quiet and noisy. Gemma 4 runs with
the language named and the repetition limit described in [Findings](#findings). The CPU rows name
the language for Whisper too.

| Engine | Device | Audio | Transcribe | Seconds per audio minute | Peak RAM | VRAM |
|--------|--------|-------|------------|--------------------------|----------|------|
| Whisper | GPU | 85 min | 141 s | 1.65 | 2.6 GiB | 2.9 GiB |
| Gemma 4 E2B | GPU | 85 min | 236 s | 2.77 | 3.4 GiB | 1.6 GiB |
| Gemma 4 E4B | GPU | 85 min | 312 s | 3.66 | 3.6 GiB | 3.4 GiB |
| Gemma 4 12B | GPU | 85 min | 874 s | 10.3 | 3.0 GiB | 7.4 GiB |
| Whisper | CPU | First 10 min | 261 s | 26.1 | 2.5 GiB | - |
| Gemma 4 E2B | CPU | First 10 min | 99 s | 9.9 | 2.6 GiB | - |
| Gemma 4 E4B | CPU | First 10 min | 184 s | 18.4 | 4.1 GiB | - |

On the GPU, a warm Gemma 4 E2B takes about 1.3 s per 30-second clip: 0.1 s to read the prompt and
the audio tokens, 0.7 s to write the answer at 115 tokens per second, and the remaining 0.5 s to
prepare the clip, which includes the audio encoder on the CPU. Gemma 4 12B reads about 240 tokens
per second and writes 45, so a 30-second clip takes it about 5 s.

### Disk

LiteRT-LM writes compiled caches next to the model file on the first load, and the first load takes
longer: 3.9 s for E2B, 13.5 s for E4B, and 21 s for 12B on the GPU.

| Model | Model file | GPU caches | CPU caches |
|-------|------------|------------|------------|
| Whisper `large-v3-turbo-q8_0` | 834 MiB | None | None |
| Gemma 4 E2B | 2.4 GiB | 0.8 GiB | 0.8 GiB |
| Gemma 4 E4B | 3.4 GiB | 2.2 GiB | 2.2 GiB |
| Gemma 4 12B | 6.4 GiB | 5.6 GiB | 5.6 GiB |

Each cache figure includes the caches of the audio encoder, up to 0.1 GiB, which both devices
share.

## Other recordings

These recordings have no human reference text, or a synthetic one. They show behavior that the
test sets of read sentences don't: silence, noise, fillers, and long audio.

### Synthetic samples with a known text

The samples speak a 57-word note with a text-to-speech voice. The note ends with voseo
("confirmame si podés llegar a tiempo"). The figures are word error rates against the text.

| Sample | Whisper | Gemma 4 E2B | Gemma 4 E4B |
|--------|---------|-------------|-------------|
| `es-short.wav`, clean | 0.0% | 3.5% | 3.5% |
| `es-noisy.wav`, loud pink noise | 0.0% | 3.5% | 3.5% |
| `es-pauses.wav`, 25 s of silence and 30 s of faint noise | 1.8% | 1.8% | 1.8% |
| `es-pauses-noise.wav`, the same over brown noise | 1.8% | 0.0% | 1.8% |
| `es-long.wav`, the note seven times in a row | 11.8% | 4.3% | 4.3% |

Every error in the first four samples is one of the two voseo words. No engine invents text in the
silence or the noise. On `es-long.wav`, Whisper drops and repeats whole sentences, with VAD and
without it (7.5%); a recording that repeats itself isn't a realistic case. Gemma 4 12B also scores
3.5% on `es-short.wav`, with the same two words.

### Real voice notes

The two WhatsApp notes have no reference text, so the comparison is between engines. Whisper's
transcript of the 24-second note reads as correct Rioplatense Spanish.

- **Fillers.** Gemma 4 keeps fillers such as "eh"; Whisper drops them. On the 4-second note, one
  filler is the only difference between the engines.
- **Voseo.** On the 24-second note, every Gemma 4 model turns voseo verbs into tuteo, and E2B and
  E4B replace one voseo verb phrase with a different verb, which changes the meaning.
- **The dialect prompt.** With it, E4B recovers that verb phrase and the voseo forms: 7 word
  differences from Whisper's 79-word transcript remain, 4 of them fillers. E2B recovers one verb and
  still gets the verb phrase wrong.

### The lecture

The table counts the words of each transcript and the share of them inside repetition loops,
which are a unit of one to six words repeated four or more times in a row.

| Engine | Setup | Words | In loops |
|--------|-------|-------|----------|
| Whisper | Server defaults | 9,038 | 2.9% |
| Gemma 4 E2B | Model card prompt | 12,104 | 24.8% |
| Gemma 4 E4B | Model card prompt | 10,473 | 12.4% |
| Gemma 4 E2B | Language named | 11,733 | 18.4% |
| Gemma 4 E4B | Language named | 11,116 | 17.5% |
| Gemma 4 E2B | Language named, repetition limit | 9,375 | 0.5% |
| Gemma 4 E4B | Language named, repetition limit | 9,326 | 0.2% |
| Gemma 4 12B | Language named, repetition limit | 9,964 | 3.3% |

- **Loops.** With the model card prompt, 10 of the 186 clips of E2B and 5 of E4B repeat one word
  until they reach the 512-token output limit. Some of those loops are in Italian, Korean, or
  Japanese. With both guards on, 7 clips of 12B still reach the limit.
- **Skipped speech.** In the first 10 minutes, with the language named for every engine, Whisper
  writes 735 words on the GPU and 820 on the CPU, E2B 1,152, and E4B 1,183. The two Gemma 4
  transcripts share sentences that Whisper's doesn't have, so Whisper skips speech there. Over the
  whole lecture, the transcripts have about the same number of words outside loops.
- **Disagreement.** With both guards on, 44% to 47% of the words differ between the E2B or E4B
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
- **Gemma 4 12B needs 16-bit activations for audio on the GPU.** With the 32-bit activations that
  LiteRtLmSharp asks for by default, 12B answers text prompts and answers an audio clip with unused
  tokens (`<unused30>`, `<mask>`) until it reaches the output limit. With `Float16` it transcribes.
  E2B and E4B don't have the problem.
- **Gemma 4 12B returns nothing for some clips.** With 16-bit activations, 6 of the 900 recordings
  of the test sets still end in an empty answer after 512 tokens, which counts as deleting every
  word of the recording.
- **Samplers on the CPU.** On the CPU backend, LiteRT-LM v0.18.0 implements only the top-p sampler
  type. The greedy and top-k types fail with "Sampler type not implemented yet". Top-p with a top-k
  cut of 1 gives greedy decoding on both backends.
- **The audio encoder runs on the CPU.** The E2B and E4B model files declare the CPU as the only
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

The code of the proof of concept isn't on the `main` branch. It's on the
[`poc/gemma-4-evaluation` branch](https://github.com/OrihuelaConde/local-whisper-mcp/tree/poc/gemma-4-evaluation),
and it needs a checkout of LiteRtLmSharp next to this repository, with its native libraries
restored, and the model files in `models/gemma`.

To build the program and compare the engines on your own audio, check out that branch and run the
following commands from the repository root:

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

To measure the word error rate on a test set, download it, transcribe it, and score it:

```powershell
python poc/scripts/fetch_testsets.py fleurs es_419 300 SET
python poc/scripts/compare_asr.py run --exe EXE --out SET --audio-dir SET/audio --language es --setup whisper-gpu gemma-e2b-gpu gemma-e4b-gpu gemma-12b-gpu
python poc/scripts/compare_asr.py score --out SET --refs SET/refs.tsv
```

Replace `SET` with a folder that git ignores, such as `test-audio/datasets/fleurs-es`. For the
Argentinian corpus, the first command is `python poc/scripts/fetch_testsets.py argentinian 300 SET`.
