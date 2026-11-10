using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Loads one speech model on demand and releases it after a period without calls.</summary>
/// <remarks>
/// Releasing the model returns its GPU memory to other processes while the server is idle. A call
/// for another model, on either engine, releases the loaded one first, so only one model holds
/// memory at a time.
/// </remarks>
internal sealed class WhisperHost : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<WhisperHost> logger;
    private readonly Timer idleTimer;
    private readonly IDisposable logForwarding;
    private ISpeechModel? model;
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

    /// <summary>Gets the device that runs the loaded model, or <see langword="null"/> if no model is loaded.</summary>
    public string? Device => model?.Device;

    /// <summary>Transcribes the samples with the requested model, loading it first if needed.</summary>
    /// <param name="modelPath">The path to the model file.</param>
    /// <param name="engine">The engine that runs the model.</param>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="request">The language and the options.</param>
    /// <param name="progress">Receives the percentage transcribed so far.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription.</returns>
    /// <exception cref="ArgumentException">The model doesn't support the language.</exception>
    public async Task<TranscriptionResult> TranscribeAsync(
        string modelPath,
        SpeechEngine engine,
        float[] samples,
        SpeechRequest request,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (model?.ModelPath != modelPath)
            {
                model?.Dispose();
                model = null;
                var started = DateTimeOffset.UtcNow;
                model = SpeechModelFactory.Create(engine, modelPath, Settings, logger);
                // whisper.cpp names its GPU only when it first transcribes, so its runtime stands in for the device here.
                logger.LogInformation(
                    "Loaded {Model} with {Engine} on {Device} in {Seconds:F2} s.",
                    Path.GetFileName(modelPath),
                    ModelCatalog.GetEngineName(engine),
                    engine == SpeechEngine.Whisper ? $"the {NativeRuntime.Loaded} runtime" : model.Device,
                    (DateTimeOffset.UtcNow - started).TotalSeconds);
            }

            try
            {
                return await model.TranscribeAsync(samples, request, progress, cancellationToken);
            }
            catch (TranscribeCpp.TranscribeCppException)
            {
                // A model whose run failed in native code may be in any state, so the next call loads it again.
                model.Dispose();
                model = null;
                logger.LogInformation("Released the model after the failure.");
                throw;
            }
        }
        finally
        {
            lastUse = DateTimeOffset.UtcNow;
            gate.Release();
        }
    }

    /// <summary>Deletes model files, releasing the loaded model first if it's one of them.</summary>
    /// <param name="fileNames">The model file names.</param>
    /// <returns>The outcome for each file.</returns>
    /// <remarks>It holds the same lock as transcriptions, so no call can load a model while its file is deleted.</remarks>
    public async Task<IReadOnlyList<ModelDeletion>> DeleteModelsAsync(IReadOnlyCollection<string> fileNames)
    {
        await gate.WaitAsync();
        try
        {
            if (model is not null && fileNames.Contains(Path.GetFileName(model.ModelPath), StringComparer.OrdinalIgnoreCase))
            {
                model.Dispose();
                model = null;
                logger.LogInformation("Released the model to delete it.");
            }

            return Models.Delete(fileNames);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Describes the device, the models, and the time left before the model is released.</summary>
    /// <returns>The current status.</returns>
    public HostStatus GetStatus()
    {
        var loaded = model;
        double? secondsUntilRelease = loaded is null
            ? null
            : Math.Round(Math.Max(0, (Settings.IdleTimeout - (DateTimeOffset.UtcNow - lastUse)).TotalSeconds));
        var runtimes = NativeRuntime.GetInstalled();

        // Clients read this field to tell the user whether the GPU is in use, so before the first
        // transcription it says what will happen instead of leaving it empty.
        var device = loaded?.Device ?? NativeRuntime.Device ?? (runtimes.Count == 0
            ? "None: no native runtime is installed next to the server."
            : $"Not chosen yet: the first transcription tries the {string.Join(", then the ", runtimes)} runtime.");
        return new HostStatus(
            ServerInfo.Version,
            device,
            runtimes,
            loaded is null ? null : ModelCatalog.GetModelName(Path.GetFileName(loaded.ModelPath)),
            loaded is null ? null : ModelCatalog.GetEngineName(loaded.Engine),
            secondsUntilRelease,
            Settings.DefaultModel,
            Settings.DefaultLanguage,
            Models.GetInstalledModelSizes(),
            Models.GetDownloads(),
            Models.ModelsDirectory,
            Settings.InboxDirectory,
            Settings.AllowedRoots,
            Settings.LogsDirectory,
            Ffmpeg.IsAvailable());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        idleTimer.Dispose();

        // A transcription still running uses the model's native memory, so the model waits for it.
        // On shutdown the wait is short: the process frees everything when it ends anyway.
        if (gate.Wait(TimeSpan.FromSeconds(5)))
        {
            model?.Dispose();
            model = null;
        }

        logForwarding.Dispose();
        gate.Dispose();
    }

    private async Task ReleaseIfIdleAsync()
    {
        if (model is null || DateTimeOffset.UtcNow - lastUse < Settings.IdleTimeout)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            if (model is not null && DateTimeOffset.UtcNow - lastUse >= Settings.IdleTimeout)
            {
                model.Dispose();
                model = null;
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
/// <param name="LoadedEngine">The engine of the loaded model, <c>whisper.cpp</c> or <c>transcribe.cpp</c>, or <see langword="null"/> if no model is loaded.</param>
/// <param name="SecondsUntilRelease">The seconds left before the idle model is released, or <see langword="null"/> if no model is loaded.</param>
/// <param name="DefaultModel">The model that calls use when they don't name one.</param>
/// <param name="DefaultLanguage">The language that calls use when they don't name one.</param>
/// <param name="InstalledModels">The speech models in the models directory, with their sizes and engines.</param>
/// <param name="Downloads">The model downloads in progress.</param>
/// <param name="ModelsDirectory">The directory that holds the models.</param>
/// <param name="Inbox">The directory to copy audio into when it isn't on this computer yet; the server deletes each file after transcribing it.</param>
/// <param name="AllowedRoots">The other directories that transcribe may read audio from; the server never deletes files there.</param>
/// <param name="Logs">The directory with the server's log files, one per day, kept for a week.</param>
/// <param name="Ffmpeg"><see langword="true"/> if ffmpeg is on the <c>PATH</c>, which adds formats such as M4A and FLAC.</param>
internal sealed record HostStatus(
    string Version,
    string Device,
    IReadOnlyList<string> AvailableRuntimes,
    string? LoadedModel,
    string? LoadedEngine,
    double? SecondsUntilRelease,
    string DefaultModel,
    string DefaultLanguage,
    IReadOnlyList<InstalledModel> InstalledModels,
    IReadOnlyList<DownloadStatus> Downloads,
    string ModelsDirectory,
    string Inbox,
    IReadOnlyList<string> AllowedRoots,
    string Logs,
    bool Ffmpeg);
