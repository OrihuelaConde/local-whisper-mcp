# Local Whisper

A skill that has Claude transcribe audio and video with Local Whisper, an MCP server that runs
Whisper on your own computer, so your recordings never leave it.

## What the plugin does

The plugin carries one skill and no code. The skill tells Claude the following:

- When to use Local Whisper: whenever you ask for a transcript, subtitles, or a summary of a
  recording, or attach one.
- How to hand over a chat attachment: in a claude.ai chat linked to your computer, Claude copies
  the file into the server's inbox folder on that computer, and the server deletes the copy after
  transcribing it.
- What to tell you when the server isn't reachable: the steps to connect it, instead of
  transcribing the recording in the cloud.

## Install

The plugin doesn't include the server. Install the Local Whisper MCP server on your computer as
described in the [installation guide](https://github.com/OrihuelaConde/local-whisper-mcp#install).

To add the plugin to Claude Code, run the following command in a session:

```text
/plugin install local-whisper --marketplace OrihuelaConde/claude-plugins
```

## Use

Ask Claude in your own words. For example:

- "Transcribe the voice note I just attached."
- "Transcribe `C:\Users\me\Recordings\meeting.m4a` and summarize the decisions."
- "Make Spanish subtitles for `~/Videos/class.mp4`."

## Data

The plugin runs nothing, makes no network requests, and stores nothing. It only instructs Claude to
call the Local Whisper tools on your computer. The server transcribes there: audio and transcripts
stay on your computer, and the server goes online only to download Whisper models from Hugging
Face. For details, see [Privacy](https://github.com/OrihuelaConde/local-whisper-mcp#privacy).

## License

Released under the [MIT License](LICENSE).
