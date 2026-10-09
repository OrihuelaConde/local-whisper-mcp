using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Loads the Whisper model on demand and releases it after a period without calls.</summary>
/// <remarks>Releasing the model returns its GPU memory to other processes while the server is idle.</remarks>
internal sealed class WhisperHost : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<WhisperHost> logger;
    private readonly Timer idleTimer;
    private readonly IDisposable logForwarding;
    private Transcriber? transcriber;
    private DateTimeOffset lastUse = DateTimeOffset.UtcNow;

    /// <summary>Initializes a new instance of the <see cref="WhisperHost"/> class.</summary>
    /// <param name="settings">The server settings.</param>
    /// <param name="models">The model store.</param>
    /// <param name="logger">The logger for load and release events.</param>
    public WhisperHost(ServerSettings settings, ModelStore models, ILogger<WhisperHost> logger)
    {
        Settings = settings;
        Models = models;
        this.logger = logger;
        NativeRuntime.Configure(settings.Runtime);
        logForwarding = NativeRuntime.ForwardLogs(logger);
        var period = TimeSpan.FromSeconds(Math.Clamp(settings.IdleTimeout.TotalSeconds / 4, 1, 30));
        idleTimer = new Timer(_ => _ = ReleaseIfIdleAsync(), null, period, period);
    }

    /// <summary>Gets the server settings.</summary>
    public ServerSettings Settings { get; }

    /// <summary>Gets the model store.</summary>
    public ModelStore Models { get; }

    /// <summary>Transcribes the samples with the requested model, loading it first if needed.</summary>
    /// <param name="modelPath">The path to the ggml model file.</param>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="language">A language code, or <c>auto</c>.</param>
    /// <param name="vadModelPath">The path to the VAD model, or <see langword="null"/> to skip VAD.</param>
    /// <param name="progress">Receives the percentage of the speech transcribed so far.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription.</returns>
    /// <exception cref="ArgumentException">Whisper doesn't support the language.</exception>
    public async Task<TranscriptionResult> TranscribeAsync(
        string modelPath,
        float[] samples,
        string language,
        string? vadModelPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (transcriber?.ModelPath != modelPath)
            {
                transcriber?.Dispose();
                transcriber = null;
                var started = DateTimeOffset.UtcNow;
                transcriber = new Transcriber(modelPath, Settings.Threads);
                logger.LogInformation("Loaded {Model} with the {Runtime} runtime in {Seconds:F2} s.", Path.GetFileName(modelPath), NativeRuntime.Loaded, (DateTimeOffset.UtcNow - started).TotalSeconds);
            }

            // The language list comes from the native library, which is loaded by now.
            if (!Transcriber.IsSupportedLanguage(language))
            {
                throw new ArgumentException($"Whisper doesn't support the language '{language}'.", nameof(language));
            }

            return await transcriber.TranscribeAsync(samples, language, vadModelPath, progress, cancellationToken);
        }
        finally
        {
            lastUse = DateTimeOffset.UtcNow;
            gate.Release();
        }
    }

    /// <summary>Describes the device, the models, and the time left before the model is released.</summary>
    /// <returns>The current status.</returns>
    public HostStatus GetStatus()
    {
        var loaded = transcriber;
        double? secondsUntilRelease = loaded is null
            ? null
            : Math.Round(Math.Max(0, (Settings.IdleTimeout - (DateTimeOffset.UtcNow - lastUse)).TotalSeconds));
        var runtimes = NativeRuntime.GetInstalled();

        // Clients read this field to tell the user whether the GPU is in use, so before the first
        // transcription it says what will happen instead of leaving it empty.
        var device = NativeRuntime.Device ?? (runtimes.Count == 0
            ? "None: no native runtime is installed next to the server."
            : $"Not chosen yet: the first transcription tries the {string.Join(", then the ", runtimes)} runtime.");
        return new HostStatus(
            ServerInfo.Version,
            device,
            runtimes,
            loaded is null ? null : ModelCatalog.GetModelName(Path.GetFileName(loaded.ModelPath)),
            secondsUntilRelease,
            Settings.DefaultModel,
            Settings.DefaultLanguage,
            Models.GetInstalledModels(),
            Models.GetDownloads(),
            Models.ModelsDirectory,
            Settings.InboxDirectory,
            Settings.AllowedRoots,
            Ffmpeg.IsAvailable());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        idleTimer.Dispose();
        transcriber?.Dispose();
        logForwarding.Dispose();
        gate.Dispose();
    }

    private async Task ReleaseIfIdleAsync()
    {
        if (transcriber is null || DateTimeOffset.UtcNow - lastUse < Settings.IdleTimeout)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            if (transcriber is not null && DateTimeOffset.UtcNow - lastUse >= Settings.IdleTimeout)
            {
                transcriber.Dispose();
                transcriber = null;
                logger.LogInformation("Released the model after {Minutes:F1} idle minutes.", Settings.IdleTimeout.TotalMinutes);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to release the model.");
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Describes the state of the server.</summary>
/// <param name="Version">The server version.</param>
/// <param name="Device">The device that runs the model, such as a GPU name or <c>CPU</c>, or the runtimes the server will try before the first transcription.</param>
/// <param name="AvailableRuntimes">The installed native runtimes in the order the server tries them; the first one that loads runs the model.</param>
/// <param name="LoadedModel">The name of the loaded model, or <see langword="null"/> if no model is loaded.</param>
/// <param name="SecondsUntilRelease">The seconds left before the idle model is released, or <see langword="null"/> if no model is loaded.</param>
/// <param name="DefaultModel">The model that calls use when they don't name one.</param>
/// <param name="DefaultLanguage">The language that calls use when they don't name one.</param>
/// <param name="InstalledModels">The models in the models directory.</param>
/// <param name="Downloads">The model downloads in progress.</param>
/// <param name="ModelsDirectory">The directory that holds the models.</param>
/// <param name="Inbox">The directory to copy audio into when it isn't on this computer yet; the server deletes each file after transcribing it.</param>
/// <param name="AllowedRoots">The other directories that transcribe may read audio from; the server never deletes files there.</param>
/// <param name="Ffmpeg"><see langword="true"/> if ffmpeg is on the <c>PATH</c>, which adds formats such as M4A and FLAC.</param>
internal sealed record HostStatus(
    string Version,
    string Device,
    IReadOnlyList<string> AvailableRuntimes,
    string? LoadedModel,
    double? SecondsUntilRelease,
    string DefaultModel,
    string DefaultLanguage,
    IReadOnlyList<string> InstalledModels,
    IReadOnlyList<DownloadStatus> Downloads,
    string ModelsDirectory,
    string Inbox,
    IReadOnlyList<string> AllowedRoots,
    bool Ffmpeg);
