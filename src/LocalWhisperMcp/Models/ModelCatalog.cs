using System.Text.RegularExpressions;

namespace LocalWhisperMcp;

/// <summary>Names the Whisper and VAD models and where to download them.</summary>
internal static partial class ModelCatalog
{
    /// <summary>The file name of the Silero VAD model.</summary>
    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>
    /// Gets the models that whisper.cpp publishes on Hugging Face, from its
    /// <c>models/download-ggml-model.sh</c> script.
    /// </summary>
    public static IReadOnlyList<string> KnownModels { get; } =
    [
        "tiny", "tiny.en", "tiny-q5_1", "tiny.en-q5_1", "tiny-q8_0",
        "base", "base.en", "base-q5_1", "base.en-q5_1", "base-q8_0",
        "small", "small.en", "small.en-tdrz", "small-q5_1", "small.en-q5_1", "small-q8_0",
        "medium", "medium.en", "medium-q5_0", "medium.en-q5_0", "medium-q8_0",
        "large-v1", "large-v2", "large-v2-q5_0", "large-v2-q8_0",
        "large-v3", "large-v3-q5_0", "large-v3-turbo", "large-v3-turbo-q5_0", "large-v3-turbo-q8_0",
    ];

    /// <summary>Checks that a model name can't escape the models directory.</summary>
    /// <param name="name">The model name, such as <c>base</c>.</param>
    /// <returns><see langword="true"/> if the name contains only letters, digits, dots, dashes, and underscores.</returns>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name) && name is not ("." or "..");

    /// <summary>Gets the file name of a Whisper model.</summary>
    /// <param name="name">The model name, such as <c>base</c>.</param>
    /// <returns>The file name, such as <c>ggml-base.bin</c>.</returns>
    public static string GetFileName(string name) => $"ggml-{name}.bin";

    /// <summary>Gets the model name of a Whisper model file.</summary>
    /// <param name="fileName">The file name, such as <c>ggml-base.bin</c>.</param>
    /// <returns>The model name, or <see langword="null"/> if the file isn't a Whisper model.</returns>
    public static string? GetModelName(string fileName) =>
        fileName.StartsWith("ggml-", StringComparison.Ordinal) && fileName.EndsWith(".bin", StringComparison.Ordinal) && fileName != VadModelFileName
            ? fileName["ggml-".Length..^".bin".Length]
            : null;

    /// <summary>Gets the download address of a model file.</summary>
    /// <param name="fileName">The file name of a known Whisper model or of the VAD model.</param>
    /// <returns>The address, or <see langword="null"/> if the file isn't a known model.</returns>
    public static Uri? GetDownloadUri(string fileName)
    {
        if (fileName == VadModelFileName)
        {
            return new Uri($"https://huggingface.co/ggml-org/whisper-vad/resolve/main/{fileName}");
        }

        return GetModelName(fileName) is { } name && KnownModels.Contains(name)
            ? new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}")
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();
}
