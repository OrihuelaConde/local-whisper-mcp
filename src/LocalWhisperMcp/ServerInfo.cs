using System.Reflection;

namespace LocalWhisperMcp;

/// <summary>Identifies the server to MCP clients and on the command line.</summary>
internal static class ServerInfo
{
    /// <summary>The server name.</summary>
    public const string Name = "local-whisper-mcp";

    /// <summary>Gets the server version, from the assembly's informational version.</summary>
    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Gets the instructions that tell the client what the server is for.</summary>
    public const string Instructions =
        "Transcribes audio files with Whisper on this computer; the audio never leaves it. " +
        "Call transcribe with the absolute path of an audio file in the inbox folder or in one of the allowed folders that status lists. " +
        "Copy audio that isn't on this computer yet, such as chat attachments, into the inbox; the server deletes it after transcribing it. " +
        "The first call downloads the default model if it's missing, which can take a few minutes.";
}
