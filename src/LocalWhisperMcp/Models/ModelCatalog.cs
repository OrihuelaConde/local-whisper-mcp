using System.Text.RegularExpressions;

namespace LocalWhisperMcp;

/// <summary>Names the Whisper and VAD models and where to download them.</summary>
internal static partial class ModelCatalog
{
    /// <summary>The file name of the Silero VAD model.</summary>
    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>
    /// Gets the models in whisper.cpp's Hugging Face repository,
    /// https://huggingface.co/ggerganov/whisper.cpp, as of October 2026.
    /// </summary>
    public static IReadOnlyList<string> KnownModels { get; } =
    [
        "tiny", "tiny-q5_1", "tiny-q8_0", "tiny.en", "tiny.en-q5_1", "tiny.en-q8_0",
        "base", "base-q5_1", "base-q8_0", "base.en", "base.en-q5_1", "base.en-q8_0",
        "small", "small-q5_1", "small-q8_0", "small.en", "small.en-q5_1", "small.en-q8_0",
        "medium", "medium-q5_0", "medium-q8_0", "medium.en", "medium.en-q5_0", "medium.en-q8_0",
        "large-v1", "large-v2", "large-v2-q5_0", "large-v2-q8_0",
        "large-v3", "large-v3-q5_0", "large-v3-turbo", "large-v3-turbo-q5_0", "large-v3-turbo-q8_0",
    ];

    /// <summary>Checks that a model name can't escape the models directory.</summary>
    /// <param name="name">The model name, such as <c>base</c>.</param>
    /// <returns><see langword="true"/> if the name contains only letters, digits, dots, dashes, and underscores.</returns>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name) && name is not ("." or "..");

    /// <summary>Accepts a model name in the file name's form too, as listed on Hugging Face.</summary>
    /// <param name="model">The model name, such as <c>base</c>, or its file name, such as <c>ggml-base.bin</c>.</param>
    /// <returns>The model name, or <see langword="null"/> if none was given.</returns>
    public static string? Normalize(string? model)
    {
        var name = model?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".bin".Length];
        }

        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name["ggml-".Length..] : name;
    }

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
