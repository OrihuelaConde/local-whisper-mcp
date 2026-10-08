# Proof of concept results

This page records the go/no-go evidence for implementing the server in C# with Native AOT and
Whisper.net instead of Python with faster-whisper. Each row is one check from the proof-of-concept
plan.

## Go/no-go table

| # | Check | Result | Evidence |
|---|-------|--------|----------|
| 1 | Minimal MCP server with `ModelContextProtocol` 2.2.0 and `PublishAot` | Go | No IL2xxx or IL3xxx warnings from the SDK. The 13 MB `win-x64` executable answers `initialize` in about 20 ms and serves `tools/list` and `tools/call` over stdio. Registration in a Claude Code session is pending. |
| 2 | Transcribe a 16 kHz mono WAV under AOT on the CPU runtime | Pending | The native library loads under AOT after the x86-64-v3 fix (see [Findings](#findings)). Waiting for a model. |
| 3 | Same as 2 with the CUDA runtime | Pending | The driver supports CUDA 13.4, but the CUDA Toolkit isn't installed. |
| 4 | Same as 2 with `Whisper.net.Runtime.Vulkan`, without the Vulkan SDK | Pending | Waiting for a model. |
| 5 | Decode Ogg Opus with Concentus, without ffmpeg, and transcribe it | Decode: go | Two real WhatsApp voice notes (4 s and 24 s, SILK wideband, 120 ms packets) and a synthetic Opus file decode under AOT with the same sample count as ffmpeg/libopus and no timestamp offset (correlation 0.995 to 1.000 against the libopus output). This required working around two Concentus bugs (see [Findings](#findings)). NLayer matches ffmpeg for MP3 (0.9997). Transcription pending. |
| 6 | Silero VAD on audio with long pauses | Pending | Implemented: speech spans from `WhisperVadFactory` are transcribed one by one. |
| 7 | Time per audio minute, RAM, and VRAM for `large-v3-turbo` full, q5_0, and q8_0 | Pending | |
| 8 | Native AOT publish and a short CPU transcription on Linux and macOS | Pending | The `poc-native-aot` workflow runs on demand only. |
| 9 | Local MCP visible in claude.ai cloud sessions as `mcp__remote-devices__<server>__<tool>` | Pending | The reference LinkedIn server is installed as a Claude Desktop extension (MCPB, manifest 0.4). |

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

## Test machine

Windows 11, Intel Core i9-14900K, 64 GB of RAM, NVIDIA GeForce RTX 3080 with 10 GB of VRAM,
driver 616.92 (CUDA 13.4), .NET SDK 10.0.401.
