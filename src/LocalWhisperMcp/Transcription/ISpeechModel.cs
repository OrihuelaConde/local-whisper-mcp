namespace LocalWhisperMcp;

/// <summary>Holds one loaded speech recognition model and transcribes 16 kHz mono samples with it.</summary>
/// <remarks>
/// The model stays in memory, including GPU memory, until the instance is disposed. Calls aren't
/// thread-safe; callers serialize access.
/// </remarks>
internal interface ISpeechModel : IDisposable
{
    /// <summary>Gets the path of the loaded model file.</summary>
    string ModelPath { get; }

    /// <summary>Gets the engine that runs the model.</summary>
    SpeechEngine Engine { get; }

    /// <summary>Gets the device that runs the model, such as a GPU name with its API, or <c>CPU</c>.</summary>
    string? Device { get; }

    /// <summary>Transcribes the samples.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="request">The language and the options; each engine uses the ones it supports.</param>
    /// <param name="progress">Receives the percentage transcribed so far.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription.</returns>
    /// <exception cref="ArgumentException">The model doesn't support the language; the parameter name is <c>language</c>.</exception>
    Task<TranscriptionResult> TranscribeAsync(float[] samples, SpeechRequest request, IProgress<int>? progress, CancellationToken cancellationToken);
}

/// <summary>Describes what to transcribe and how.</summary>
/// <param name="Language">A language code, or <c>auto</c>.</param>
/// <param name="VadModelPath">The path to the VAD model, or <see langword="null"/> to skip VAD. Whisper only.</param>
/// <param name="Prompt">Text that the model reads as if it came right before the audio, or <see langword="null"/> for none. Whisper only.</param>
/// <param name="CarryPrompt">Whether every 30-second window reads the prompt, not only the first ones. Whisper only.</param>
internal sealed record SpeechRequest(string Language, string? VadModelPath = null, string? Prompt = null, bool CarryPrompt = false);

/// <summary>Creates the model object for a model file's engine.</summary>
internal static class SpeechModelFactory
{
    /// <summary>Loads a model.</summary>
    /// <param name="engine">The engine that runs the model.</param>
    /// <param name="modelPath">The path to the model file.</param>
    /// <param name="settings">The server settings, for the runtime and the thread count.</param>
    /// <param name="logger">The logger for the engine's messages.</param>
    /// <returns>The loaded model.</returns>
    public static ISpeechModel Create(SpeechEngine engine, string modelPath, ServerSettings settings, Microsoft.Extensions.Logging.ILogger logger) => engine switch
    {
        SpeechEngine.TranscribeCpp => new ParakeetModel(modelPath, settings.Runtime, settings.Threads, logger),
        _ => new WhisperModel(modelPath, settings.Threads),
    };
}
