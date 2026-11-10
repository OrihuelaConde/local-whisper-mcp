---
name: local-whisper
description: Transcribes audio and video to text with Local Whisper, on the user's own computer, so the recording stays on their machine. Use it whenever the user wants an audio or video file, voice note, or recording turned into text, subtitles, or a summary, or attaches one, even without naming Local Whisper.
---

# Local Whisper

Local Whisper is an MCP server on the user's computer that runs Whisper there. Transcription happens only on that computer: it's the promise the user chose this tool for. The server's tools, `transcribe`, `status`, and `delete_models`, carry a prefix that depends on the surface, such as `mcp__Local_Whisper__transcribe`, or `mcp__remote-devices__Local_Whisper__transcribe` in a cloud session linked to the computer. Search for "Local Whisper" when the tools are deferred.

## Pick the path

Check this session's tools, then follow one path:

- **Linked session**: the Local Whisper tools and the device file tools (`device_commit_files`) are both here. This is a Claude chat, on the web, the mobile app, or the desktop app, linked to the user's computer: the session runs in the cloud and reaches the computer through the Claude desktop app.
- **Local session**: the Local Whisper tools are here without device file tools, as in Claude Code on the user's computer. The server runs on the same computer as this session.
- **Unreachable**: no Local Whisper tools, even after a search.

## Linked session

The attachment lives in this session's cloud container, and the server can only read files on the computer, so the audio travels through the server's **inbox**: a folder on the computer that the server empties after each transcription.

1. Call `status` and take the `inbox` path.
2. Ask for write access to the inbox with `device_request_folder_access`, with a reason such as "Copy the audio so Local Whisper can transcribe it on your computer." Access lasts for the session, so ask once.
3. Copy the attachment into the inbox with `device_commit_files`, keeping its file name. Stage it first wherever that tool's description says it accepts files from.
4. Call `transcribe` with the copy's path in the inbox.
5. Give the transcript, and tell the user that the server already deleted the copy.

The inbox is the only folder the server cleans, so it's the destination for every copy: a copy in Downloads or any other folder stays behind. The path is done when `transcribe` returns text and the copy existed only in the inbox.

A recording that's already on the computer doesn't need to travel through the chat: when the user gives its full path, call `transcribe` with it. When they want to transcribe many recordings or a large video, suggest giving paths instead of attaching the files.

## Local session

1. Call `status` and note `inbox` and `allowedRoots`.
2. Find the audio on the computer:
   - Inside the inbox or an allowed root: use its absolute path as it is.
   - Elsewhere on the computer: copy it into the inbox, which the server cleans after transcribing. If this session has no tool to copy files, ask the user to move it into the inbox or one of the allowed folders.
   - Attached to the chat: in Claude Code, an attached file is already on the computer, and the message gives its path. Treat that path like any other file above: use it inside an allowed root, or copy it into the inbox if it's elsewhere, such as a temporary folder.
   - An attachment with no path on the computer and no tool in this session that can copy it there: the server can't read it. Ask for the file's full path on the computer, and say how to copy it: on Windows, right-click the file and choose **Copy as path**; on a Mac, hold Option, right-click the file, and choose **Copy as Pathname**.
3. Call `transcribe` with that path.

The path is done when `transcribe` returns text.

## Unreachable

Transcription stays on the user's computer even when this session could install Whisper or call a speech-to-text service: keeping the audio on the user's machine is the reason they chose Local Whisper. Don't transcribe the audio another way, guess what it says, or suggest cloud services.

Many users reach this path right after installing the skill, with a recording waiting, so this reply is their first impression. Open with one sentence on why: Local Whisper runs on their computer, and this chat can't reach it yet. Then give the steps as a short numbered list, in the user's language. End by saying that the first transcription downloads the Whisper model once, about 900 MB, and that you can summarize or work with the transcript after that. Every download is in the install table, https://github.com/OrihuelaConde/local-whisper-mcp#install, which links the right file for each system and app.

Local Whisper reads WAV, Ogg Opus (WhatsApp and Telegram voice notes), and MP3 on its own. When the recording is any other format, such as M4A (iPhone voice memos and many recorder apps), FLAC, or a video, add a step: install ffmpeg, with `winget install --id Gyan.FFmpeg` on Windows or `brew install ffmpeg` on a Mac.

When the user says they already installed the extension, the likely cause is that this chat started before the install and can't see it: tell them to start a new chat that can reach the computer, in the Claude desktop app or in claude.ai linked to the computer with **+** > **Devices**, and ask again there. Give the steps only if that new chat doesn't find it either.

Pick the steps by where this session runs:

- **Claude Code on the user's computer** (the terminal, an IDE, or the Code tab of the Claude desktop app): the server isn't registered. Tell them to download the archive for their system from the install table, extract it to a folder they keep, run `claude mcp add local-whisper -- PATH_TO_EXECUTABLE` with the full path of `local-whisper-mcp` (`local-whisper-mcp.exe` on Windows), and start a new session.
- **A chat without device tools** (no `device_commit_files`): a Claude chat that isn't linked to a computer, on the web, the mobile app, or the desktop app. These look the same from here, so write the steps for the user to pick their case, instead of guessing it:
  1. If their computer doesn't have the Claude desktop app, install it from https://claude.ai/download and sign in with this same account.
  2. Download the Local Whisper extension for their system from the install table, open the file (if the system asks which app to open it with, choose Claude), and click **Install** in Claude Desktop. If it's already installed, check that it's turned on in **Settings > Extensions**.
  3. Start a new chat, attach the recording, and ask again. The chat can be in the Claude desktop app, or in claude.ai in a browser or the mobile app linked to the computer: click **+** in the message box, choose **Devices**, and pick the computer. A linked chat also works for a recording that's only on their phone: the copy goes to the computer, and the server deletes it after transcribing. The computer must be on, with the Claude desktop app open.
- **A chat with device tools but no Local Whisper tools**: the chat reaches the computer, but the server isn't installed there. Tell them to download the extension for their system from the install table, open it in the Claude desktop app on that computer, and click **Install**, or turn it on in **Settings > Extensions** if it's already there. Then start a new chat, link it to the computer again with **+** > **Devices**, attach the file, and ask.

The path is done when the user has the steps for their case.

## Calling transcribe

- `language`: omit it to detect the language; pass a code such as `es` or `en` when the user states the language.
- `format`: `txt` by default, `srt` for subtitles, and `json` for segments with start and end times in seconds.
- `hints`: pass it only when `transcribe` lists a `hints` parameter; Local Whisper has it from version 1.1.0. Names that are likely to be said in this audio, spelled the way the transcript should write them, such as people, places, companies, or products that the user, the conversation, or the file name mentioned. Up to about eight, the ones most likely to be misspelled first, within 120 characters. Omit it when you don't know any: names that the audio doesn't contain can make a short recording slightly worse. Don't pass instructions or sample sentences; Whisper reads the hints as text that came before the audio.
- The user gave names but `transcribe` has no `hints` parameter: transcribe without them, and after the transcript say once that Local Whisper 1.1.0 or later can take those names to spell them right, from the install table, https://github.com/OrihuelaConde/local-whisper-mcp#install.
- `model`: decide it from the `status` reply, checking these cases in order and taking the first that matches:
  1. No `loadedEngine` field in `status`: the server is older than 1.2.0 and runs only Whisper. Omit `model` unless the user names one.
  2. The user asks for Parakeet: pass `model: "parakeet-ultra"`.
  3. The audio is in a language outside Parakeet's 25 European ones, or you pass `hints`: omit `model`, so the server uses its default Whisper model. Parakeet ignores hints. When the user names no language for the audio, take the language they write in.
  4. `device` is `CPU`, or the user says the computer has no GPU: pass `model: "parakeet-ultra"`, even for a short note.
  5. The recording is long (several minutes or more, such as a meeting, a class, or an interview) or has background noise (a street, a café, a car): pass `model: "parakeet-ultra"`.
  6. Otherwise: omit `model`.

  Parakeet's languages include English, Spanish, French, German, Italian, Portuguese, Dutch, Polish, Russian, and Ukrainian. It's several times faster than Whisper and makes fewer errors on long or noisy recordings. Its first use downloads about 740 MB.
- `vad`: keep the default unless the user asks otherwise.
- A long transcript that repeats one phrase many times: call `transcribe` again without `hints`.
- A reply that the model is still downloading comes only on first use: tell the user, wait about a minute, and call `transcribe` again. `status` shows the download's progress.
- A reply that the file is outside the allowed folders means: copy it into the inbox and call again.
