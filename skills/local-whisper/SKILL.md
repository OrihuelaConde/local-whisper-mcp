---
name: local-whisper
description: Transcribes audio to text with Local Whisper, on the user's own computer, so the audio stays on their machine. Use it whenever the user wants an audio file, voice note, or recording turned into text, subtitles, or a summary, or attaches audio, even without naming Local Whisper.
---

# Local Whisper

Local Whisper is an MCP server on the user's computer that runs Whisper there. Transcription happens only on that computer: it's the promise the user chose this tool for. The server has two tools, `transcribe` and `status`, under a prefix that depends on the surface, such as `mcp__Local_Whisper__transcribe`, or `mcp__remote-devices__Local_Whisper__transcribe` in a cloud session linked to the computer. Search for "Local Whisper" when the tools are deferred.

## Pick the path

Check this session's tools, then follow one path:

- **Linked session**: the Local Whisper tools and the device file tools (`device_commit_files`) are both here. The session runs in the cloud and reaches the computer through the Claude desktop app.
- **Local session**: the Local Whisper tools are here without device file tools. The server runs on the same computer as this session.
- **Unreachable**: no Local Whisper tools, even after a search.

## Linked session

The attachment lives in this session's cloud container, and the server can only read files on the computer, so the audio travels through the server's **inbox**: a folder on the computer that the server empties after each transcription.

1. Call `status` and take the `inbox` path.
2. Ask for write access to the inbox with `device_request_folder_access`, with a reason such as "Copy the audio so Local Whisper can transcribe it on your computer." Access lasts for the session, so ask once.
3. Copy the attachment into the inbox with `device_commit_files`, keeping its file name. Stage it first wherever that tool's description says it accepts files from.
4. Call `transcribe` with the copy's path in the inbox.
5. Give the transcript, and tell the user that the server already deleted the copy.

The inbox is the only folder the server cleans, so it's the destination for every copy: a copy in Downloads or any other folder stays behind. The path is done when `transcribe` returns text and the copy existed only in the inbox.

## Local session

1. Call `status` and note `inbox` and `allowedRoots`.
2. Find the audio on the computer:
   - Inside the inbox or an allowed root: use its absolute path as it is.
   - Elsewhere on the computer: copy it into the inbox, which the server cleans after transcribing.
   - Only in the chat, as an attachment: ask the user for the file's path on their computer.
3. Call `transcribe` with that path.

The path is done when `transcribe` returns text.

## Unreachable

Transcription stays on the user's computer even when this session could install Whisper or call a speech-to-text service: keeping the audio on the user's machine is the reason they chose Local Whisper. The reply is the steps to connect it, first and short, with at most one sentence on why. Pick the steps by the tools this session has:

- **No device tools** (no `device_commit_files`): the chat isn't running on the user's computer. Tell them to start a new chat in claude.ai, click **+** in the message box, choose **Devices**, and pick their computer under **Run tasks on**; then attach the audio again and ask. The computer must be on, with the Claude desktop app open.
- **Device tools but no Local Whisper tools**: the chat runs on the computer, but the server isn't installed there. Tell them to install the Local Whisper extension in the Claude desktop app from https://github.com/OrihuelaConde/local-whisper-mcp, then start a new chat on that computer and ask again.

The path is done when the user has the steps for their case.

## Calling transcribe

- `language`: omit it to detect the language; pass a code such as `es` or `en` when the user states the language.
- `format`: `txt` by default, `srt` for subtitles, and `json` for segments with start and end times in seconds.
- `vad` and `model`: keep the defaults unless the user asks otherwise.
- A reply that the model is still downloading comes only on first use: tell the user, wait about a minute, and call `transcribe` again. `status` shows the download's progress.
- A reply that the file is outside the allowed folders means: copy it into the inbox and call again.
