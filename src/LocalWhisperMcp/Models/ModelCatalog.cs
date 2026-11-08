using System.Text.RegularExpressions;

namespace LocalWhisperMcp;

/// <summary>Names the speech and VAD models, the engine that runs each one, and where to download them.</summary>
/// <remarks>
/// Every Whisper model is a <c>ggml-NAME.bin</c> file, including ones the catalog doesn't know, which
/// the user can copy into the models directory. A model for another engine must be in
/// <see cref="OtherModels"/>: the server doesn't run a GGUF file it doesn't know.
/// </remarks>
internal static partial class ModelCatalog
{
    /// <summary>The file name of the Silero VAD model.</summary>
    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>The name of the Parakeet model.</summary>
    public const string ParakeetUltra = "parakeet-ultra";

    /// <summary>
    /// Gets the models in whisper.cpp's Hugging Face repository,
    /// https://huggingface.co/ggerganov/whisper.cpp, as of October 2026.
    /// </summary>
    public static IReadOnlyList<string> WhisperModels { get; } =
    [
        "tiny", "tiny-q5_1", "tiny-q8_0", "tiny.en", "tiny.en-q5_1", "tiny.en-q8_0",
        "base", "base-q5_1", "base-q8_0", "base.en", "base.en-q5_1", "base.en-q8_0",
        "small", "small-q5_1", "small-q8_0", "small.en", "small.en-q5_1", "small.en-q8_0",
        "medium", "medium-q5_0", "medium-q8_0", "medium.en", "medium.en-q5_0", "medium.en-q8_0",
        "large-v1", "large-v2", "large-v2-q5_0", "large-v2-q8_0",
        "large-v3", "large-v3-q5_0", "large-v3-turbo", "large-v3-turbo-q5_0", "large-v3-turbo-q8_0",
    ];

    /// <summary>Gets the models that run on an engine other than Whisper.</summary>
    /// <remarks>
    /// Parakeet-ultra is Moondream's post-trained parakeet-tdt-0.6b-v3, converted to GGUF by the
    /// transcribe.cpp authors, under the CC-BY-4.0 license. Only the Q8_0 file is offered: it
    /// measured as accurate as the larger ones.
    /// </remarks>
    public static IReadOnlyList<CatalogModel> OtherModels { get; } =
    [
        new(
            ParakeetUltra,
            SpeechEngine.TranscribeCpp,
            "parakeet-ultra-Q8_0.gguf",
            new Uri("https://huggingface.co/handy-computer/parakeet-ultra-gguf/resolve/main/parakeet-ultra-Q8_0.gguf")),
    ];

    /// <summary>Gets every model the server can download: the Whisper models, then the others.</summary>
    public static IReadOnlyList<string> KnownModels { get; } = [.. WhisperModels, .. OtherModels.Select(model => model.Name)];

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

        if (OtherModels.FirstOrDefault(other => other.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) || other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return known.Name;
        }

        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".bin".Length];
        }

        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name["ggml-".Length..] : name;
    }

    /// <summary>Gets the engine that runs a model.</summary>
    /// <param name="name">The model name, such as <c>base</c> or <c>parakeet-ultra</c>.</param>
    /// <returns>The engine; every model outside <see cref="OtherModels"/> is a Whisper model.</returns>
    public static SpeechEngine GetEngine(string name) => Find(name)?.Engine ?? SpeechEngine.Whisper;

    /// <summary>Gets the file name of a model.</summary>
    /// <param name="name">The model name, such as <c>base</c>.</param>
    /// <returns>The file name, such as <c>ggml-base.bin</c>.</returns>
    public static string GetFileName(string name) => Find(name)?.FileName ?? $"ggml-{name}.bin";

    /// <summary>Gets the model name of a model file.</summary>
    /// <param name="fileName">The file name, such as <c>ggml-base.bin</c>.</param>
    /// <returns>The model name, or <see langword="null"/> if the file isn't a speech model the server runs.</returns>
    public static string? GetModelName(string fileName)
    {
        if (OtherModels.FirstOrDefault(model => model.FileName == fileName) is { } other)
        {
            return other.Name;
        }

        return fileName.StartsWith("ggml-", StringComparison.Ordinal) && fileName.EndsWith(".bin", StringComparison.Ordinal) && fileName != VadModelFileName
            ? fileName["ggml-".Length..^".bin".Length]
            : null;
    }

    /// <summary>Gets the download address of a model file.</summary>
    /// <param name="fileName">The file name of a known model or of the VAD model.</param>
    /// <returns>The address, or <see langword="null"/> if the file isn't a known model.</returns>
    public static Uri? GetDownloadUri(string fileName)
    {
        if (fileName == VadModelFileName)
        {
            return new Uri($"https://huggingface.co/ggml-org/whisper-vad/resolve/main/{fileName}");
        }

        if (OtherModels.FirstOrDefault(model => model.FileName == fileName) is { } other)
        {
            return other.DownloadUri;
        }

        return GetModelName(fileName) is { } name && WhisperModels.Contains(name)
            ? new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}")
            : null;
    }

    /// <summary>Gets the name that status and tool replies use for an engine.</summary>
    /// <param name="engine">The engine.</param>
    /// <returns><c>whisper.cpp</c> or <c>transcribe.cpp</c>.</returns>
    public static string GetEngineName(SpeechEngine engine) => engine == SpeechEngine.TranscribeCpp ? "transcribe.cpp" : "whisper.cpp";

    private static CatalogModel? Find(string name) => OtherModels.FirstOrDefault(model => model.Name == name);

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();
}

/// <summary>Names the engines that run speech models.</summary>
internal enum SpeechEngine
{
    /// <summary>whisper.cpp, through Whisper.net.</summary>
    Whisper,

    /// <summary>transcribe.cpp, which runs Parakeet models.</summary>
    TranscribeCpp,
}

/// <summary>Describes a model that the catalog knows.</summary>
/// <param name="Name">The model name that calls and settings use.</param>
/// <param name="Engine">The engine that runs it.</param>
/// <param name="FileName">The file name in the models directory.</param>
/// <param name="DownloadUri">Where to download it.</param>
internal sealed record CatalogModel(string Name, SpeechEngine Engine, string FileName, Uri DownloadUri);
