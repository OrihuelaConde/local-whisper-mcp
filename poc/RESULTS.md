# Proof of concept results

This page records the go/no-go evidence for implementing the server in C# with Native AOT and
Whisper.net instead of Python with faster-whisper. Each row is one check from the proof-of-concept
plan.

## Go/no-go table

| # | Check | Result | Evidence |
|---|-------|--------|----------|
| 1 | Minimal MCP server with `ModelContextProtocol` 2.2.0 and `PublishAot` | Go | No IL2xxx or IL3xxx warnings from the SDK. The 14 MB `win-x64` executable answers `initialize` in about 25 ms and serves `tools/list` and `tools/call` over stdio, including real transcriptions. A call from a Claude Code session is pending. |
| 2 | Transcribe a 16 kHz mono WAV under AOT on the CPU runtime | Go | With the x86-64-v3 fix (see [Findings](#findings)), the AOT build loads the CPU runtime and transcribes. `base` on 8 threads: 2.7 s per audio minute, 0.2 s to load, 364 MiB peak RAM, and a word-perfect transcript of the synthetic Spanish note. |
| 3 | Same as 2 with the CUDA runtime | Postponed | The driver supports CUDA 13.4, but the CUDA Toolkit isn't installed. Vulkan already gives GPU speed without installing anything, so CUDA waits for a reason to need it. |
| 4 | Same as 2 with `Whisper.net.Runtime.Vulkan`, without the Vulkan SDK | Go | The Vulkan loader that ships with the NVIDIA driver (`vulkan-1.dll`) is enough: whisper.cpp finds the RTX 3080 with cooperative matrix support (`NV_coopmat2`). `base`: 0.75 s per audio minute. The first run on a machine takes about 7 s longer while the driver compiles and caches the shaders. |
| 5 | Decode Ogg Opus with Concentus, without ffmpeg, and transcribe it | Go | Two real WhatsApp voice notes (4 s and 24 s, SILK wideband, 120 ms packets) and a synthetic Opus file decode under AOT with the same sample count as ffmpeg/libopus and no timestamp offset (correlation 0.995 to 1.000 against the libopus output). This required working around two Concentus bugs (see [Findings](#findings)). `large-v3-turbo` transcribes the 24-second note almost word for word, including Rioplatense voseo; `base` misses several words. NLayer matches ffmpeg for MP3 (0.9997). |
| 6 | Silero VAD on audio with long pauses | Go | On a synthetic note with 25 s of silence and 30 s of noise, no model invents text, but without VAD timestamps snap to 30-second windows and land 4.1 s and 11.9 s early. With VAD, segments start within 0.2 s of the measured speech onsets (34.11 s and 71.92 s). Joining the speech spans before transcribing makes VAD cheaper, not costlier: `base` on CPU takes 0.8 s with VAD against 1.6 s without it. Hallucinations in real, noisy silence still need a real recording. |
| 7 | Time per audio minute, RAM, and VRAM for `large-v3-turbo` full, q5_0, and q8_0 | Go | See [Measurements](#measurements). On the RTX 3080 with Vulkan, q8_0 is the best trade-off: 0.95 s per audio minute and 1.3 GiB of VRAM. On the CPU every variant takes 27 to 34 s per audio minute. |
| 8 | Native AOT publish and a short CPU transcription on Linux and macOS | Pending | The `poc-native-aot` workflow runs on demand only. |
| 9 | Local MCP visible in claude.ai cloud sessions as `mcp__remote-devices__<server>__<tool>` | Go | With the `.mcpb` extension installed in Claude Desktop, a Cowork session in the cloud copied an attached WhatsApp note to the PC, called `transcribe` with its absolute path, and returned the same transcript as the local tests. The exact tool name in the cloud session wasn't checked. See [Findings](#findings) for the folder prompt and the leftover copy. |

## Measurements

`large-v3-turbo` on a 170-second synthetic Spanish note (`--language es`, no VAD), on the test
machine described below. Vulkan figures are from the second run; the first run of a new executable
takes about 5 s longer while the driver compiles shaders. VRAM is the increase in GPU memory in use
while the process runs. CPU runs use 8 threads.

| Model | Size | Runtime | Load | Seconds per audio minute | Peak RAM | VRAM |
|-------|------|---------|------|--------------------------|----------|------|
| q5_0 | 547 MiB | Vulkan | 0.6 s | 1.45 | 646 MiB | 1.1 GiB |
| q8_0 | 834 MiB | Vulkan | 0.8 s | 0.95 | 936 MiB | 1.3 GiB |
| full (f16) | 1.5 GiB | Vulkan | 1.4 s | 0.99 | 1.1 GiB | 2.0 GiB |
| q5_0 | 547 MiB | CPU | 0.4 s | 31.1 | 1.1 GiB | - |
| q8_0 | 834 MiB | CPU | 0.6 s | 26.7 | 1.4 GiB | - |
| full (f16) | 1.5 GiB | CPU | 1.1 s | 34.3 | 2.1 GiB | - |

The MCP server releases the model after the idle timeout: GPU memory in use went from 2,344 MiB to
3,194 MiB after the first call with q8_0 on Vulkan, back to 2,359 MiB after the timeout, and the next
call reloaded the model in 0.8 s.

## Findings

- **AVX detection under Native AOT.** Whisper.net 1.9.1 chooses its CPU runtime with
  `Avx.IsSupported`, `Avx2.IsSupported`, and `Fma.IsSupported`. Native AOT compiles for x86-64-v2 by
  default, where those checks return `false`, so the loader throws `PlatformNotSupportedException`
  on a CPU that supports AVX2. Setting `IlcInstructionSet` to `x86-64-v3` fixes it. The
  Whisper.net CPU runtime already requires AVX2, so the setting doesn't drop any supported CPU.
- **One AOT warning from Whisper.net.** `NativeLibraryLoader` reads `Assembly.Location` (IL3000),
  which is empty under AOT. The loader also probes `AppDomain.CurrentDomain.BaseDirectory`, so the
  native libraries load anyway.
- **No `ForcedRuntimeLibrary`.** Whisper.net 1.9.1 has no `RuntimeOptions.ForcedRuntimeLibrary`.
  `RuntimeOptions.RuntimeLibraryOrder` selects or forces a runtime.
- **Vulkan on Linux.** `Whisper.net.Runtime.Vulkan` 1.9.1 ships `linux-x64` binaries in addition to
  `win-x64`, although its README lists only Windows x64.
- **Metal on Apple Silicon.** The `macos-arm64` binaries in `Whisper.net.Runtime` include the Metal
  backend, so Apple Silicon gets GPU acceleration without a separate package.
- **Desktop RIDs.** `Whisper.net.Runtime` ships CPU binaries for `win-x64`, `win-arm64`, `win-x86`,
  `linux-x64`, `linux-arm64`, `linux-arm`, `osx-x64` (as `macos-x64`), and `osx-arm64`.
- **Publish size.** The publish output copies native binaries for every platform. The Vulkan
  backend alone is 58 MB per platform. A release build must keep only the target RID.
- **Concentus.OggFile hangs on WhatsApp voice notes.** WhatsApp ends its Ogg streams without the
  end-of-stream flag. On the 24-second note, `OpusOggReadStream.HasNextPacket` never turns `false`,
  so the JIT build loops forever and the AOT build crashes. The proof of concept replaces it with a
  small Ogg demuxer (`OggReader`) and drops the package.
- **Concentus fails on zero-length Opus frames.** WhatsApp uses discontinuous transmission (DTX):
  during silence, a 120 ms packet carries six 20 ms SILK frames and some of them are empty.
  Concentus 2.2.2 throws `OpusException` for any packet with an empty frame (9 of 199 packets in
  the 24-second note). The proof of concept splits each packet into frames (`OpusPacket`), decodes
  each frame as a single-frame packet, and runs packet loss concealment for empty frames, which is
  what libopus does.
- **Opus pre-skip and MP3 delay.** The Ogg reader drops the Opus pre-skip and trims the end to the
  last granule position, so the output lines up with ffmpeg sample for sample. NLayer still doesn't
  drop the MP3 encoder delay (69 ms in the test file); the final decoder should trim it.
- **MSIX packages get private views of `AppData`.** Claude Desktop (`Claude_pzs8sxrjxfjjc`) and the
  Python install manager are MSIX packages, and the processes they start inherit their package's
  view of `AppData`. Files that a process under Claude Desktop writes to `%LOCALAPPDATA%` or
  `%APPDATA%` land in `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache`, invisible to
  processes of other packages: models that curl saved under `%LOCALAPPDATA%\local-whisper-mcp\models`
  didn't exist for Python, and whisper.cpp launched from Python failed to open them. The server
  therefore defaults to `~/.local-whisper-mcp/models`, which every client sees; the server that
  Claude Desktop starts found the models there. The LinkedIn extension uses `~/.linkedin-mcp` for
  the same reason.
- **Cloud sessions copy the audio to the PC first.** Before calling `transcribe`, the Cowork
  session asked the user for access to `~/.local-whisper-mcp` and copied the attachment there as
  `audio-tmp.ogg`. It had no permission to delete the copy afterwards, so the audio stayed on disk.
  A dedicated inbox folder, which the server empties after each transcription, would make the
  prompt a one-time approval and avoid leftover copies.
- **`status` before the first call.** The session called `status` before transcribing and reported
  "no GPU assigned" because `device` is `none` until the model loads. `status` should report the
  runtime the server will try first.
- **Claude Desktop extensions.** The LinkedIn server that appears in cloud sessions is an MCP Bundle
  (`.mcpb`) with server type `uv`, which means Claude Desktop manages its Python environment. MCPB
  also supports the `binary` server type, which fits a per-platform AOT executable.

## Build notes

- **Native AOT on Windows with Visual Studio 18.** The ILCompiler `findvcvarsall.bat` script calls
  Visual Studio's `vcvarsall.bat`, which runs `vswhere.exe` without a full path. If the Visual
  Studio Installer folder isn't on the `PATH`, the error text ends up in the linker path and the
  link step fails with exit code 123. To publish, add the folder to the `PATH` first:

  ```powershell
  $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
  dotnet publish poc/AotMcp -c Release -r win-x64
  ```

- **Stale Native AOT intermediates.** Once, after source changes, ILC failed with
  `Object reference not set to an instance of an object`. Deleting `obj/Release` and publishing
  again fixed it.

## Test machine

Windows 11, Intel Core i9-14900K, 64 GB of RAM, NVIDIA GeForce RTX 3080 with 10 GB of VRAM,
driver 616.92 (CUDA 13.4), .NET SDK 10.0.401.
