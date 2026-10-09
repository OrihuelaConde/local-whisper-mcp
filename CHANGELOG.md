# Changelog

This file records the notable changes in each release of Local Whisper MCP. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow publishes each
version's section as its release notes.

## [Unreleased]

### Added

- Claude Code plugin with the Local Whisper skill, listed in the `orihuelaconde` marketplace
  (`OrihuelaConde/claude-plugins`).

### Changed

- When the server isn't reachable from Claude Code, the skill tells the user how to register it
  with `claude mcp add` instead of giving the steps for claude.ai.
- The skill also triggers for video files and names the `delete_models` tool.

## [1.0.0] - 2026-10-09

The first public release. Versions before 1.0.0 were internal test builds.

### Added

- MCP server over stdio that transcribes audio files on the user's computer with Whisper
  (whisper.cpp through Whisper.net), published as a Native AOT executable for `win-x64`,
  `linux-x64`, `linux-arm64`, `osx-arm64`, and `osx-x64`.
- `transcribe` tool with the parameters `path`, `model`, `language`, `format` (`txt`, `srt`, or
  `json`), and `vad`. Plain text breaks into paragraphs at pauses of 2 seconds or more.
- `status` tool that reports the device in use, the installed models and their sizes, downloads in
  progress, the inbox, the allowed folders, and the log folder.
- `delete_models` tool that deletes the named models, or all of them, to free disk space.
- GPU transcription through Vulkan on Windows and Linux x64 and through Metal on Apple Silicon,
  with automatic fallback to the CPU.
- Decoding of WAV (8- to 32-bit integer, floating point, and `WAVE_FORMAT_EXTENSIBLE`), Ogg Opus,
  and MP3 without ffmpeg, matching ffmpeg's output. Other formats and the audio of video files go
  through ffmpeg, which the server finds on the `PATH`, in package manager folders, or at
  `LOCAL_WHISPER_FFMPEG`.
- Model downloads from Hugging Face on first use, checked against the published SHA-256, that
  continue in the background when a call returns first, and a `download` command to fetch models
  ahead of time. The default model is `large-v3-turbo-q8_0`.
- Voice activity detection with Silero VAD on clean recordings, decided from the spread between the
  recording's loud and quiet moments, with the loudness evened out before detection.
- The model leaves memory, including GPU memory, after 5 idle minutes by default.
- Allowed folders for reading audio, and an inbox at `~/.local-whisper-mcp/inbox` for audio that
  clients copy to the computer, which the server deletes after transcribing it.
- Daily log files in `~/.local-whisper-mcp/logs`, kept for a week, that never contain transcripts
  and leave file names out of routine entries.
- Claude Desktop extension (`.mcpb`) for Windows and macOS, with a folder picker for the folders
  with audio, checkboxes for the GPU and automatic downloads, a number field for the idle minutes,
  and an optional file picker for ffmpeg.
- Local Whisper skill for claude.ai, which tells Claude how to use the server in each kind of chat
  and how to connect it when it isn't reachable.
- Settings through environment variables: `LOCAL_WHISPER_MODEL`, `LOCAL_WHISPER_LANGUAGE`,
  `LOCAL_WHISPER_USE_GPU`, `LOCAL_WHISPER_RUNTIME`, `LOCAL_WHISPER_IDLE_MINUTES`,
  `LOCAL_WHISPER_ALLOWED_ROOTS`, `LOCAL_WHISPER_INBOX_DIR`, `LOCAL_WHISPER_MODELS_DIR`,
  `LOCAL_WHISPER_AUTO_DOWNLOAD`, `LOCAL_WHISPER_THREADS`, and `LOCAL_WHISPER_FFMPEG`.

[Unreleased]: https://github.com/OrihuelaConde/local-whisper-mcp/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/OrihuelaConde/local-whisper-mcp/releases/tag/v1.0.0
