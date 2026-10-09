# Local Whisper MCP

Private, local audio transcription for Claude. Local Whisper is a Model Context Protocol (MCP)
server that transcribes audio files with Whisper on your own computer, so voice notes, meetings,
and lectures never leave it. It works in Claude Desktop, in Claude Code, and in claude.ai chats
that run on your computer.

## Features

- Transcribes WAV, Ogg Opus (WhatsApp and Telegram voice notes), and MP3 files with no extra
  software. With [ffmpeg](https://ffmpeg.org) on the `PATH`, it also reads M4A and FLAC.
- Transcribes videos, such as MP4 and WebM files, when ffmpeg is installed: the server takes the
  audio track and ignores the picture. Turn a recorded meeting or class into text or subtitles
  without extracting the audio first.
- Runs on the GPU through Vulkan on Windows and Linux and through Metal on Apple Silicon, and falls
  back to the CPU on its own. With the default model, `large-v3-turbo-q8_0`, an NVIDIA GeForce
  RTX 3080 transcribes a minute of audio in about one second.
- Downloads the model the first time it's needed, checks it against the hash that Hugging Face
  publishes, and frees its memory, including GPU memory, after 5 minutes without use.
- Returns plain text, SubRip subtitles (SRT), or JSON segments with timestamps.
- Skips silence with voice activity detection on clean recordings, which keeps timestamps exact,
  and transcribes everything when background noise could hide quiet speech.
- Ships as one native executable per platform: there's no Python or .NET runtime to install.

## Install

Download the files from the [latest release](https://github.com/OrihuelaConde/local-whisper-mcp/releases/latest).
Each section below names the file it needs.

### Claude Desktop

To add Local Whisper to Claude Desktop, install its extension:

1. Download the extension for your system: `local-whisper-mcp-VERSION-win-x64.mcpb` for Windows,
   `local-whisper-mcp-VERSION-osx-arm64.mcpb` for Apple Silicon Macs, or
   `local-whisper-mcp-VERSION-osx-x64.mcpb` for Intel Macs.
2. Open the file. Claude Desktop shows the extension's details.
3. Click **Install**.
4. Optional: In the extension's settings, choose the folders with your audio, the model, and the
   language.

### Claude Code

To add Local Whisper to Claude Code, register the executable as an MCP server:

1. Download the archive for your system, such as `local-whisper-mcp-VERSION-linux-x64.tar.gz`, and
   extract it to a folder that you keep.
2. Run the following command:

   ```bash
   claude mcp add local-whisper -- PATH_TO_EXECUTABLE
   ```

   Replace `PATH_TO_EXECUTABLE` with the full path of `local-whisper-mcp` (or
   `local-whisper-mcp.exe` on Windows) in that folder.

To change a setting, add it to the command as an environment variable, for example
`claude mcp add local-whisper -e LOCAL_WHISPER_LANGUAGE=es -- PATH_TO_EXECUTABLE`.

### claude.ai chats on your computer

A claude.ai chat reaches the server through the Claude desktop app on your computer:

1. Install the extension in Claude Desktop, as described in [Claude Desktop](#claude-desktop).
2. When you start a chat in claude.ai, click **+** in the message box, choose **Devices**, and pick
   your computer under **Run tasks on**. The computer has to be on, with the Claude desktop app
   open.
3. Attach the audio and ask Claude to transcribe it. Claude copies the file into the server's inbox
   folder, and the server deletes the copy after transcribing it.

### The skill

The skill is optional, and recommended. The server works without it, but the skill tells Claude
when to use the server, how to pass it a chat attachment, and what to tell you when the server
isn't reachable. Without it, a claude.ai chat that doesn't run on your computer can't see the server
and may try to transcribe the audio in the cloud instead of telling you how to connect it.

To add the skill to claude.ai, download `local-whisper-skill.zip` and upload it in
**Settings > Capabilities**, under **Skills**.

## Use

Ask Claude in your own words. For example:

- "Transcribe the voice note I just attached."
- "Transcribe `C:\Users\me\Recordings\meeting.m4a` and summarize the decisions."
- "Make Spanish subtitles for `~/Videos/class.mp4`."

Video files are much larger than audio files. When a video is already on your computer, give
Claude its path instead of attaching it to the chat, so it doesn't have to travel through the chat
and back.

The server provides three tools:

| Tool | What it does |
|------|--------------|
| `transcribe` | Transcribes an audio file. Its parameters are `path` (absolute), `model`, `language`, `format` (`txt`, `srt`, or `json`), and `vad`. |
| `status` | Reports the device in use, the installed models and their sizes, downloads in progress, the inbox, the allowed folders, and the log folder. |
| `delete_models` | Deletes downloaded models to free disk space. Its description tells Claude to confirm with you first. |

## Configure

The Claude Desktop extension shows these settings on its page. For Claude Code and other MCP
clients, set them as environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `LOCAL_WHISPER_MODEL` | `large-v3-turbo-q8_0` | Any model from [whisper.cpp's model list](https://huggingface.co/ggerganov/whisper.cpp/tree/main), by name (`base`) or file name (`ggml-base.bin`). |
| `LOCAL_WHISPER_LANGUAGE` | `auto` | A language code such as `en` or `es`, or `auto` to detect it. |
| `LOCAL_WHISPER_USE_GPU` | `true` | `false` runs only on the CPU. |
| `LOCAL_WHISPER_RUNTIME` | `auto` | `auto`, `cpu`, or `vulkan`. A runtime that can't load falls back to the CPU. |
| `LOCAL_WHISPER_IDLE_MINUTES` | `5` | Minutes without use before the model leaves memory. `0` frees it after every transcription. |
| `LOCAL_WHISPER_ALLOWED_ROOTS` | Your user folder | Folders the server may read audio from, separated by `;` on Windows and `:` elsewhere. The `--allowed-roots DIR...` option replaces it. |
| `LOCAL_WHISPER_INBOX_DIR` | `~/.local-whisper-mcp/inbox` | Folder for audio that clients copy to the computer. The server deletes each file there after transcribing it. |
| `LOCAL_WHISPER_MODELS_DIR` | `~/.local-whisper-mcp/models` | Folder for the downloaded models. |
| `LOCAL_WHISPER_AUTO_DOWNLOAD` | `true` | `false` stops the server from downloading missing models. |
| `LOCAL_WHISPER_THREADS` | Up to 8 | CPU threads for transcription. |
| `LOCAL_WHISPER_FFMPEG` | Found automatically | Full path of the ffmpeg executable. Without it, the server looks on the `PATH` and in the folders of winget, Scoop, Chocolatey, Homebrew, and MacPorts. |

To download a model before its first use, for example on a slow connection, run the executable with
the `download` command:

```bash
local-whisper-mcp download large-v3-turbo-q8_0
```

## Privacy

- Transcription runs on your computer. The server goes online only to download models from
  Hugging Face, which you can turn off with `LOCAL_WHISPER_AUTO_DOWNLOAD=false`.
- The server reads audio only inside the allowed folders and the inbox, and it deletes only files in
  the inbox: each one after transcribing it, and any left there for more than a day.
- The log in `~/.local-whisper-mcp/logs` records timings, settings, and errors, never transcripts.
  Routine entries leave out file names, but an error entry can include the path of the file that
  failed, so review the log before you share it. The server keeps a week of logs.

## Requirements

- Windows 10 or later (x64), macOS (Apple Silicon or Intel), or Linux (x64 or arm64).
- On x64, a processor with AVX2.
- For the GPU: a Vulkan driver on Windows and Linux x64, or Apple Silicon for Metal. Without one,
  the server uses the CPU.
- Optional: ffmpeg, for formats other than WAV, Ogg Opus, and MP3.

The release files aren't signed. On macOS, if Gatekeeper blocks the executable from the archive,
allow it with `xattr -dr com.apple.quarantine FOLDER`, where `FOLDER` is the extracted folder.

## Troubleshoot

- **The first transcription takes minutes.** The server is downloading the model, about 900 MB for
  the default one. Claude can follow the download with the `status` tool.
- **The first transcription on a Mac with Apple Silicon takes about 15 seconds more.** macOS
  compiles the GPU shaders once per computer.
- **Parts of a recording are missing.** Ask Claude to transcribe it again with `vad` set to
  `false`.
- **Something else fails.** Check today's file in `~/.local-whisper-mcp/logs`.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and the
[Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites)
for your system. To run the tests, publish the server, and pack the Claude Desktop extension, run
these commands from the repository root:

```bash
dotnet test --project tests/LocalWhisperMcp.Tests
```

```bash
dotnet publish src/LocalWhisperMcp -c Release -r win-x64 -o out
```

```bash
pwsh packaging/mcpb/pack.ps1 -Rid win-x64 -PublishDir out
```

Replace `win-x64` with the runtime identifier of your system: `osx-arm64`, `osx-x64`, `linux-x64`,
or `linux-arm64`. Native AOT builds only for the system it runs on.

## License

Local Whisper is released under the [MIT License](LICENSE). It includes components under their own
licenses, listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Local Whisper is an independent project, not affiliated with or endorsed by Anthropic or OpenAI.
Claude is a trademark of Anthropic, PBC, and this project uses the name only to say which apps it
works with.
