using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LocalWhisperMcp;

/// <summary>Provides the MCP tools of the server.</summary>
[McpServerToolType]
internal sealed class TranscriptionTools
{
    // MCP clients time out long calls, so a call waits this long for a model download and then
    // returns, while the download continues in the background.
    private static readonly TimeSpan DownloadWait = TimeSpan.FromSeconds(40);

    /// <summary>Transcribes an audio file on this machine.</summary>
    /// <param name="host">The model host, from dependency injection.</param>
    /// <param name="logger">The logger, from dependency injection.</param>
    /// <param name="progress">Sends progress notifications to the client, if it asked for them.</param>
    /// <param name="path">The absolute path to the audio file.</param>
    /// <param name="model">The model name, or <see langword="null"/> for the default model.</param>
    /// <param name="language">A language code, <c>auto</c>, or <see langword="null"/> for the default language.</param>
    /// <param name="format">One of <c>txt</c>, <c>srt</c>, or <c>json</c>.</param>
    /// <param name="vad">Whether to transcribe only the spans where voice activity detection finds speech, or <see langword="null"/> to decide from the background noise.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcript in the requested format.</returns>
    [McpServerTool(Name = "transcribe", Title = "Transcribe audio", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Transcribes an audio file on this computer with Whisper. The audio never leaves the computer. " +
        "Reads WAV, Ogg Opus (WhatsApp and Telegram voice notes), and MP3; other formats, such as M4A, need ffmpeg. " +
        "The file must be in the inbox folder or in one of the allowed folders that the status tool lists. " +
        "To transcribe a file that isn't on this computer yet, such as a chat attachment, copy it into the inbox: " +
        "the server deletes files in the inbox after transcribing them, so no copy is left behind.")]
    public static async Task<string> TranscribeAsync(
        WhisperHost host,
        ILogger<TranscriptionTools> logger,
        IProgress<ProgressNotificationValue> progress,
        [Description("Absolute path to the audio file.")] string path,
        [Description("Whisper model, such as large-v3-turbo-q8_0 or base. Omit it to use the server default, which suits almost every case.")] string? model = null,
        [Description("Language of the audio as a code such as en or es, or auto to detect it. Omit it to use the server default.")] string? language = null,
        [Description("Output: txt for plain text, srt for subtitles, or json for segments with start and end times in seconds.")] string format = "txt",
        [Description("Voice activity detection, which skips silence and noise. Omit it: the server turns it on for clean recordings, where it keeps timestamps exact, and off when background noise could hide quiet speech from it. Pass false if speech goes missing, or true to force it.")] bool? vad = null,
        CancellationToken cancellationToken = default)
    {
        var settings = host.Settings;
        var audioPath = AudioPathPolicy.Resolve(path, settings.ReadableRoots);

        format = format.Trim().ToLowerInvariant();
        if (!TranscriptFormatter.Formats.Contains(format))
        {
            throw new McpException($"Unknown format '{format}'. Use txt, srt, or json.");
        }

        language = language?.Trim().ToLowerInvariant() is { Length: > 0 } requested ? requested : settings.DefaultLanguage;
        if (!Languages.IsWellFormed(language))
        {
            throw new McpException($"'{language}' isn't a language code. Use a code such as en or es, or auto.");
        }

        var modelName = ModelCatalog.Normalize(model) ?? settings.DefaultModel;
        if (!ModelCatalog.IsValidName(modelName))
        {
            throw new McpException($"Invalid model name '{modelName}'.");
        }

        // Decoding first reports a file that can't be read before any model download starts.
        float[] samples;
        try
        {
            samples = await AudioDecoder.DecodeAsync(audioPath, logger, cancellationToken);
        }
        catch (AudioDecodingException exception)
        {
            throw new McpException(exception.Message, exception);
        }

        var modelPath = await EnsureModelAsync(host, modelName, progress, DownloadWait, cancellationToken);

        var useVad = vad ?? true;
        if (vad is null)
        {
            var spread = AudioQuality.MeasureSpreadDb(samples);
            useVad = spread >= AudioQuality.CleanSpreadDb;
            logger.LogInformation("The level spread is {Spread:F1} dB, so VAD is {State}.", spread, useVad ? "on" : "off");
        }

        var vadPath = useVad ? await TryEnsureVadModelAsync(host, logger, cancellationToken) : null;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await host.TranscribeAsync(modelPath, samples, language, vadPath, new PercentProgress(progress), cancellationToken);
            logger.LogInformation(
                "Transcribed {Seconds:F1} s of {Extension} audio ({Speech:F1} s of speech) with {Model} on {Device} in {Elapsed:F2} s.",
                result.Duration.TotalSeconds,
                Path.GetExtension(audioPath).ToLowerInvariant(),
                result.Speech.Sum(s => (s.End - s.Start).TotalSeconds),
                modelName,
                NativeRuntime.Device,
                stopwatch.Elapsed.TotalSeconds);
            var transcript = TranscriptFormatter.Format(result, format);

            // Only a successful transcription consumes the file; after a failure the client can
            // retry, and Inbox.Prepare removes what's left after a day.
            Inbox.DeleteIfInside(audioPath, settings.InboxDirectory, logger);
            return transcript;
        }
        catch (ArgumentException exception) when (exception.ParamName == "language")
        {
            throw new McpException($"Whisper doesn't support the language '{language}'. Use a code such as en or es, or auto.", exception);
        }
    }

    /// <summary>Describes the server: device, models, downloads, and the folders it may read.</summary>
    /// <param name="host">The model host, from dependency injection.</param>
    /// <returns>The current status.</returns>
    [McpServerTool(Name = "status", Title = "Transcription status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Reports the device that runs Whisper (a GPU or the CPU), " +
        "the loaded, default, and installed models, model downloads in progress, " +
        "the inbox folder for audio that isn't on this computer yet, and the allowed folders that transcribe may read audio from.")]
    public static HostStatus GetStatus(WhisperHost host) => host.GetStatus();

    /// <summary>Deletes downloaded models to free disk space.</summary>
    /// <param name="host">The model host, from dependency injection.</param>
    /// <param name="models">The models to delete, or <see langword="null"/> to delete all of them.</param>
    /// <returns>What was deleted and how much space it freed.</returns>
    [McpServerTool(Name = "delete_models", Title = "Delete downloaded models", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Deletes downloaded Whisper models to free disk space; status lists them with their sizes. " +
        "Without model names, it deletes every model, including the voice activity detection model. " +
        "A deleted model downloads again the next time a transcription needs it. Confirm with the user before calling it.")]
    public static async Task<string> DeleteModelsAsync(
        WhisperHost host,
        [Description("Models to delete, such as base or large-v3-turbo-q5_0. Omit it to delete all of them.")] string[]? models = null)
    {
        IReadOnlyList<string> fileNames;
        if (models is { Length: > 0 })
        {
            var names = models.Select(ModelCatalog.Normalize).OfType<string>().ToList();
            if (names.FirstOrDefault(name => !ModelCatalog.IsValidName(name)) is { } invalid)
            {
                throw new McpException($"Invalid model name '{invalid}'.");
            }

            fileNames = [.. names.Select(ModelCatalog.GetFileName)];
        }
        else
        {
            fileNames = [.. host.Models.GetModelFiles().Select(file => file.Name)];
        }

        if (fileNames.Count == 0)
        {
            return "There are no downloaded models to delete.";
        }

        var results = await host.DeleteModelsAsync(fileNames);
        var deleted = results.Where(result => result.Reason is null).ToList();
        var lines = new List<string>();
        if (deleted.Count > 0)
        {
            lines.Add($"Deleted {deleted.Count} {(deleted.Count == 1 ? "model" : "models")} and freed {deleted.Sum(result => result.Bytes) / 1e6:N0} MB:");
            lines.AddRange(deleted.Select(result => $"- {DisplayName(result.FileName)} ({result.Bytes / 1e6:N0} MB)"));
        }

        lines.AddRange(results.Where(result => result.Reason is not null).Select(result => $"Kept {DisplayName(result.FileName)}: {result.Reason}."));
        return string.Join('\n', lines);

        static string DisplayName(string fileName) =>
            fileName == ModelCatalog.VadModelFileName ? "the voice activity detection model" : ModelCatalog.GetModelName(fileName) ?? fileName;
    }

    /// <summary>Gets the path of a model, downloading it first if it's missing and downloads are allowed.</summary>
    /// <param name="host">The model host.</param>
    /// <param name="modelName">The model name.</param>
    /// <param name="progress">Receives the download progress.</param>
    /// <param name="wait">How long to wait for a download before returning while it continues.</param>
    /// <param name="cancellationToken">The token to stop waiting; it doesn't stop the download.</param>
    /// <returns>The path of the model file.</returns>
    /// <exception cref="McpException">The model is missing and can't be downloaded now.</exception>
    internal static async Task<string> EnsureModelAsync(
        WhisperHost host,
        string modelName,
        IProgress<ProgressNotificationValue> progress,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        var models = host.Models;
        var fileName = ModelCatalog.GetFileName(modelName);
        if (!File.Exists(models.GetPath(fileName)))
        {
            var installed = models.GetInstalledModels() is { Count: > 0 } list ? string.Join(", ", list) : "none";
            if (ModelCatalog.GetDownloadUri(fileName) is null)
            {
                throw new McpException(
                    $"Model '{modelName}' isn't installed in {models.ModelsDirectory} and isn't a model this server can download. " +
                    $"Installed models: {installed}. Downloadable models: {string.Join(", ", ModelCatalog.KnownModels)}.");
            }

            if (!host.Settings.AutoDownload)
            {
                throw new McpException(
                    $"Model '{modelName}' isn't installed in {models.ModelsDirectory} and automatic downloads are off. " +
                    $"Installed models: {installed}. Run 'local-whisper-mcp download {modelName}', or set LOCAL_WHISPER_AUTO_DOWNLOAD to true.");
            }
        }

        var download = models.Ensure(fileName);
        var waited = Stopwatch.StartNew();
        while (!download.Completion.IsCompleted)
        {
            var status = download.GetStatus();
            progress.Report(new ProgressNotificationValue
            {
                Progress = status.BytesReceived,
                Total = status.TotalBytes,
                Message = $"Downloading the {modelName} model",
            });
            if (waited.Elapsed >= wait)
            {
                var size = status.TotalBytes is { } total ? $"{total / 1e6:F0} MB, " : string.Empty;
                var done = status.Percent is { } percent ? $"{percent:F0}% done" : $"{status.BytesReceived / 1e6:F0} MB so far";
                throw new McpException(
                    $"The {modelName} model is still downloading ({size}{done}); the download continues in the background. " +
                    "Call transcribe again in a minute or two, or call status to follow the download.");
            }

            await Task.WhenAny(download.Completion, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            return await download.Completion;
        }
        catch (Exception exception)
        {
            throw new McpException(
                $"Couldn't download the {modelName} model: {exception.Message} " +
                $"Check the network connection, or download {ModelCatalog.GetDownloadUri(fileName)} into {models.ModelsDirectory}.",
                exception);
        }
    }

    private static async Task<string?> TryEnsureVadModelAsync(WhisperHost host, ILogger logger, CancellationToken cancellationToken)
    {
        var models = host.Models;
        var path = models.GetPath(ModelCatalog.VadModelFileName);
        if (File.Exists(path))
        {
            return path;
        }

        if (!host.Settings.AutoDownload)
        {
            logger.LogWarning("The VAD model isn't installed in {Directory}, and automatic downloads are off; transcribing without VAD.", models.ModelsDirectory);
            return null;
        }

        try
        {
            // The VAD model is under 1 MB.
            return await models.Ensure(ModelCatalog.VadModelFileName).Completion.WaitAsync(DownloadWait, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Couldn't get the VAD model; transcribing without VAD.");
            return null;
        }
    }

    /// <summary>Forwards Whisper's percentage to MCP progress notifications.</summary>
    /// <param name="target">The progress that sends notifications to the client.</param>
    private sealed class PercentProgress(IProgress<ProgressNotificationValue> target) : IProgress<int>
    {
        /// <inheritdoc/>
        public void Report(int value) => target.Report(new ProgressNotificationValue
        {
            Progress = value,
            Total = 100,
            Message = "Transcribing",
        });
    }
}
