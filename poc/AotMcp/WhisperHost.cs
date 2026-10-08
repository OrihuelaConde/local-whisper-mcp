using System.Globalization;
using Microsoft.Extensions.Logging;
using PocCore;

/// <summary>Holds the server settings, read from environment variables.</summary>
/// <param name="ModelsDirectory">The directory that contains the ggml model files.</param>
/// <param name="DefaultModel">The model name to use when a call doesn't specify one.</param>
/// <param name="DefaultLanguage">The language to use when a call doesn't specify one.</param>
/// <param name="Runtime">The preferred native runtime: <c>auto</c>, <c>cpu</c>, <c>vulkan</c>, or <c>cuda</c>.</param>
/// <param name="IdleTimeout">The time without calls after which the model is released.</param>
/// <param name="AllowedRoots">The directories that the server may read audio from.</param>
/// <param name="Threads">The number of CPU threads to use.</param>
public sealed record WhisperSettings(
    string ModelsDirectory,
    string DefaultModel,
    string DefaultLanguage,
    string Runtime,
    TimeSpan IdleTimeout,
    IReadOnlyList<string> AllowedRoots,
    int Threads)
{
    /// <summary>Gets the file name of the Silero VAD model inside <see cref="ModelsDirectory"/>.</summary>
    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>Reads the settings from the environment.</summary>
    /// <returns>The settings, with defaults for every variable that isn't set.</returns>
    public static WhisperSettings FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Not LocalApplicationData: on Windows, MSIX-packaged clients such as Claude Desktop see a
        // virtualized AppData, so models stored there by other processes are invisible to the server.
        var dataDirectory = Path.Combine(home, ".local-whisper-mcp", "models");
        var roots = Get("LOCAL_WHISPER_ALLOWED_ROOTS", home)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();

        return new WhisperSettings(
            Get("LOCAL_WHISPER_MODELS_DIR", dataDirectory),
            Get("LOCAL_WHISPER_MODEL", "base"),
            Get("LOCAL_WHISPER_LANGUAGE", "auto"),
            Get("LOCAL_WHISPER_RUNTIME", "auto").ToLowerInvariant(),
            TimeSpan.FromMinutes(double.Parse(Get("LOCAL_WHISPER_IDLE_MINUTES", "10"), CultureInfo.InvariantCulture)),
            roots,
            int.Parse(Get("LOCAL_WHISPER_THREADS", Math.Min(Environment.ProcessorCount, 8).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));

        static string Get(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
    }
}

/// <summary>Loads the Whisper model on demand and releases it after a period without calls.</summary>
/// <remarks>Releasing the model returns its GPU memory to other processes while the server is idle.</remarks>
public sealed class WhisperHost : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<WhisperHost> logger;
    private readonly Timer idleTimer;
    private Transcriber? transcriber;
    private DateTimeOffset lastUse = DateTimeOffset.UtcNow;

    /// <summary>Initializes a new instance of the <see cref="WhisperHost"/> class.</summary>
    /// <param name="settings">The server settings.</param>
    /// <param name="logger">The logger for load and release events.</param>
    public WhisperHost(WhisperSettings settings, ILogger<WhisperHost> logger)
    {
        Settings = settings;
        this.logger = logger;
        Transcriber.ConfigureRuntime(settings.Runtime);
        var period = TimeSpan.FromSeconds(Math.Clamp(settings.IdleTimeout.TotalSeconds / 4, 1, 30));
        idleTimer = new Timer(_ => _ = ReleaseIfIdleAsync(), null, period, period);
    }

    /// <summary>Gets the server settings.</summary>
    public WhisperSettings Settings { get; }

    /// <summary>Transcribes the samples with the requested model, loading it first if needed.</summary>
    /// <param name="modelPath">The path to the ggml model file.</param>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="language">A two-letter language code, or <c>auto</c>.</param>
    /// <param name="vadModelPath">The path to the VAD model, or <see langword="null"/> to skip VAD.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        string modelPath,
        float[] samples,
        string language,
        string? vadModelPath,
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
                logger.LogInformation("Loaded {Model} on {Runtime} in {Seconds:F2} s.", Path.GetFileName(modelPath), Transcriber.LoadedRuntime, (DateTimeOffset.UtcNow - started).TotalSeconds);
            }

            return await transcriber.TranscribeAsync(samples, language, vadModelPath, cancellationToken);
        }
        finally
        {
            lastUse = DateTimeOffset.UtcNow;
            gate.Release();
        }
    }

    /// <summary>Describes the runtime, the loaded model, and the time left before it's released.</summary>
    /// <returns>The current status.</returns>
    public HostStatus GetStatus()
    {
        var loaded = transcriber;
        double? secondsUntilRelease = loaded is null
            ? null
            : Math.Max(0, (Settings.IdleTimeout - (DateTimeOffset.UtcNow - lastUse)).TotalSeconds);
        return new HostStatus(
            Transcriber.LoadedRuntime,
            loaded is null ? null : Path.GetFileName(loaded.ModelPath),
            secondsUntilRelease,
            Settings.DefaultModel,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        idleTimer.Dispose();
        transcriber?.Dispose();
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
/// <param name="Device">The native runtime in use, such as <c>Cpu</c> or <c>Vulkan</c>, or <c>none</c> before the first load.</param>
/// <param name="LoadedModel">The file name of the loaded model, or <see langword="null"/> if no model is loaded.</param>
/// <param name="SecondsUntilRelease">The seconds left before the idle model is released, or <see langword="null"/> if no model is loaded.</param>
/// <param name="DefaultModel">The model that calls use when they don't specify one.</param>
/// <param name="RuntimeIdentifier">The runtime identifier (RID) of the server process.</param>
/// <param name="NativeAot"><see langword="true"/> when the server runs as Native AOT code.</param>
public sealed record HostStatus(
    string Device,
    string? LoadedModel,
    double? SecondsUntilRelease,
    string DefaultModel,
    string RuntimeIdentifier,
    bool NativeAot);
