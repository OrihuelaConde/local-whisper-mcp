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

The plugin doesn't include the server, so install Local Whisper on your computer first. Each
download is in the [install table](https://github.com/OrihuelaConde/local-whisper-mcp#install).

To use it in claude.ai or the Claude desktop app, follow these steps:

1. Install the [Claude desktop app](https://claude.ai/download) on your computer, and sign in.
2. Download the Local Whisper extension for your system, open the file, and click **Install** in
   Claude Desktop.
3. Start a new chat in Claude Desktop and ask Claude to transcribe a file by its path. To attach
   the file instead, start a claude.ai chat linked to your computer: click **+** in the message
   box, choose **Devices**, and pick your computer under **Run tasks on**.

To use it in Claude Code, register the server as described in the
[Claude Code guide](https://github.com/OrihuelaConde/local-whisper-mcp#claude-code), and then run
the following command in a session to add the plugin:

```text
/plugin install local-whisper --marketplace OrihuelaConde/claude-plugins
```

If you ask for a transcript before the server is reachable, Claude gives you these steps instead of
sending the recording anywhere else.

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
