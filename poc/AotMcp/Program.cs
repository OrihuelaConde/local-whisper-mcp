using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PocCore;

var builder = Host.CreateApplicationBuilder(args);

// The stdio transport owns stdout, so every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Explicit registration instead of WithToolsFromAssembly: assembly scanning relies on
// reflection that trimming can't analyze.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
serializerOptions.TypeInfoResolverChain.Insert(0, PocJsonContext.Default);

builder.Services.AddSingleton(WhisperSettings.FromEnvironment());
builder.Services.AddSingleton<WhisperHost>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<WhisperTools>(serializerOptions);

await builder.Build().RunAsync();

/// <summary>Provides the transcription tools.</summary>
[McpServerToolType]
public sealed partial class WhisperTools
{
    /// <summary>Transcribes an audio file on this machine.</summary>
    /// <param name="host">The model host, from dependency injection.</param>
    /// <param name="path">The absolute path to the audio file.</param>
    /// <param name="model">The model name, such as <c>base</c> or <c>large-v3-turbo-q5_0</c>.</param>
    /// <param name="language">A two-letter language code, or <c>auto</c> to detect it.</param>
    /// <param name="format">One of <c>txt</c>, <c>srt</c>, or <c>json</c>.</param>
    /// <param name="vad">Whether to transcribe only the spans where voice activity detection finds speech.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcript in the requested format.</returns>
    [McpServerTool(Name = "transcribe", ReadOnly = true, OpenWorld = false)]
    [Description("Transcribes an audio file on this machine with Whisper. The audio never leaves the machine.")]
    public static async Task<string> TranscribeAsync(
        WhisperHost host,
        [Description("Absolute path to the audio file (wav, ogg, opus, mp3; other formats need ffmpeg).")] string path,
        [Description("Model name, for example base or large-v3-turbo-q5_0. Omit to use the server default.")] string? model = null,
        [Description("Two-letter language code, or auto to detect it. Omit to use the server default.")] string? language = null,
        [Description("Output format: txt, srt, or json (segments with timestamps).")] string format = "txt",
        [Description("Skip silence and noise with voice activity detection before transcribing.")] bool vad = true,
        CancellationToken cancellationToken = default)
    {
        var settings = host.Settings;
        var audioPath = ResolveAudioPath(path, settings.AllowedRoots);
        var modelName = model is { Length: > 0 } ? model : settings.DefaultModel;
        if (!ModelNamePattern().IsMatch(modelName))
        {
            throw new McpException($"Invalid model name '{modelName}'.");
        }

        var modelPath = Path.Combine(settings.ModelsDirectory, $"ggml-{modelName}.bin");
        if (!File.Exists(modelPath))
        {
            throw new McpException($"Model '{modelName}' isn't installed in {settings.ModelsDirectory}.");
        }

        var vadPath = Path.Combine(settings.ModelsDirectory, WhisperSettings.VadModelFileName);
        var samples = await AudioLoader.LoadAsync(audioPath);
        var result = await host.TranscribeAsync(
            modelPath,
            samples,
            language is { Length: > 0 } ? language : settings.DefaultLanguage,
            vad && File.Exists(vadPath) ? vadPath : null,
            cancellationToken);
        return TranscriptFormatter.Format(result.Segments, format.ToLowerInvariant());
    }

    /// <summary>Describes the device, the loaded model, and the time left before the model is released.</summary>
    /// <param name="host">The model host, from dependency injection.</param>
    /// <returns>The current status.</returns>
    [McpServerTool(Name = "status", ReadOnly = true, OpenWorld = false)]
    [Description("Reports the device (CPU or GPU runtime), the loaded model, and the seconds left before the idle model is released.")]
    public static HostStatus GetStatus(WhisperHost host) => host.GetStatus();

    private static string ResolveAudioPath(string path, IReadOnlyList<string> allowedRoots)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new McpException("The path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!allowedRoots.Any(root => fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison)))
        {
            throw new McpException("The path is outside the allowed roots. Set LOCAL_WHISPER_ALLOWED_ROOTS to allow it.");
        }

        if (!File.Exists(fullPath))
        {
            throw new McpException($"File not found: {fullPath}");
        }

        return fullPath;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ModelNamePattern();
}

[JsonSerializable(typeof(HostStatus))]
internal sealed partial class PocJsonContext : JsonSerializerContext;
