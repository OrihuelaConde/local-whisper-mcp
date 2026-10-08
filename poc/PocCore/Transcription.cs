using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace PocCore;

/// <summary>Holds one loaded Whisper model and transcribes 16 kHz mono samples with it.</summary>
/// <remarks>
/// The model stays in memory, including GPU memory, until the instance is disposed. Calls aren't
/// thread-safe; callers serialize access.
/// </remarks>
public sealed class Transcriber : IDisposable
{
    private readonly WhisperFactory factory;
    private readonly int threads;

    /// <summary>Initializes a new instance of the <see cref="Transcriber"/> class and loads the model.</summary>
    /// <param name="modelPath">The path to the ggml model file.</param>
    /// <param name="threads">The number of CPU threads to use.</param>
    public Transcriber(string modelPath, int threads)
    {
        ModelPath = modelPath;
        this.threads = threads;
        factory = WhisperFactory.FromPath(modelPath);

        // Creating a processor forces the model to load now instead of on the first transcription.
        using var warmup = factory.CreateBuilder().Build();
    }

    /// <summary>Gets the path of the loaded model.</summary>
    public string ModelPath { get; }

    /// <summary>Gets the native runtime that Whisper.net loaded, such as CPU or Vulkan.</summary>
    public static string LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString() ?? "none";

    /// <summary>Selects the native runtimes that Whisper.net may load, in order of preference.</summary>
    /// <param name="runtime">One of <c>auto</c>, <c>cpu</c>, <c>vulkan</c>, or <c>cuda</c>.</param>
    /// <remarks>Only takes effect before the first model loads.</remarks>
    public static void ConfigureRuntime(string runtime)
    {
        // CPU stays last in every list, so a machine without the GPU runtime still transcribes.
        RuntimeOptions.RuntimeLibraryOrder = runtime switch
        {
            "cpu" => [RuntimeLibrary.Cpu],
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu],
            _ => RuntimeOptions.RuntimeLibraryOrder,
        };
    }

    /// <summary>Transcribes the samples, optionally only the spans where the VAD model detects speech.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="language">A two-letter language code, or <c>auto</c> to detect it.</param>
    /// <param name="vadModelPath">The path to the Silero VAD model, or <see langword="null"/> to transcribe everything.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription and the speech spans that were transcribed.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        float[] samples,
        string language,
        string? vadModelPath,
        CancellationToken cancellationToken = default)
    {
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioLoader.SampleRate);
        var spans = vadModelPath is null ? [new SpeechSpan(TimeSpan.Zero, duration)] : DetectSpeech(samples, vadModelPath);

        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(threads)
            .Build();

        var segments = new List<TranscriptSegment>();
        foreach (var span in spans)
        {
            var first = (int)Math.Min(samples.Length, span.Start.TotalSeconds * AudioLoader.SampleRate);
            var last = (int)Math.Min(samples.Length, span.End.TotalSeconds * AudioLoader.SampleRate);
            await foreach (var result in processor.ProcessAsync(samples.AsMemory(first, last - first), cancellationToken))
            {
                segments.Add(new TranscriptSegment(span.Start + result.Start, span.Start + result.End, result.Text.Trim(), result.Language));
            }
        }

        return new TranscriptionResult(segments, spans, duration);
    }

    /// <inheritdoc/>
    public void Dispose() => factory.Dispose();

    private SpeechSpan[] DetectSpeech(float[] samples, string vadModelPath)
    {
        using var vadFactory = WhisperVadFactory.FromPath(vadModelPath);
        using var vad = vadFactory.CreateBuilder().WithThreads(threads).Build();
        return [.. vad.DetectSpeech(samples).Select(s => new SpeechSpan(s.Start, s.End))];
    }
}

/// <summary>Represents a span of audio that contains speech.</summary>
/// <param name="Start">The start of the span.</param>
/// <param name="End">The end of the span.</param>
public sealed record SpeechSpan(TimeSpan Start, TimeSpan End);

/// <summary>Represents one transcribed segment with timestamps from the beginning of the audio.</summary>
/// <param name="Start">The start of the segment.</param>
/// <param name="End">The end of the segment.</param>
/// <param name="Text">The transcribed text.</param>
/// <param name="Language">The detected language code.</param>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, string Language);

/// <summary>Represents the outcome of one transcription.</summary>
/// <param name="Segments">The transcribed segments.</param>
/// <param name="Speech">The spans that were transcribed.</param>
/// <param name="Duration">The duration of the whole audio.</param>
public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<SpeechSpan> Speech, TimeSpan Duration)
{
    /// <summary>Gets the total duration of the transcribed speech spans.</summary>
    public TimeSpan SpeechDuration => TimeSpan.FromTicks(Speech.Sum(s => (s.End - s.Start).Ticks));
}

/// <summary>Formats a transcript as plain text, SubRip, or JSON.</summary>
public static class TranscriptFormatter
{
    /// <summary>Formats the segments in the requested format.</summary>
    /// <param name="segments">The transcribed segments.</param>
    /// <param name="format">One of <c>txt</c>, <c>srt</c>, or <c>json</c>.</param>
    /// <returns>The formatted transcript.</returns>
    public static string Format(IReadOnlyList<TranscriptSegment> segments, string format) => format switch
    {
        "srt" => ToSrt(segments),
        "json" => JsonSerializer.Serialize([.. segments], TranscriptJsonContext.Default.TranscriptSegmentArray),
        _ => string.Join(' ', segments.Select(s => s.Text)),
    };

    private static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1}\n{Timestamp(segments[i].Start)} --> {Timestamp(segments[i].End)}\n{segments[i].Text}\n\n");
        }

        return builder.ToString();
    }

    private static string Timestamp(TimeSpan value) => value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
}

/// <summary>Writes mono samples as a 16-bit PCM WAV file.</summary>
public static class WaveWriter
{
    /// <summary>Writes the samples to a WAV file.</summary>
    /// <param name="path">The output path.</param>
    /// <param name="samples">The samples, in the range from -1 to 1.</param>
    /// <param name="sampleRate">The sample rate, in hertz.</param>
    public static void Write(string path, float[] samples, int sampleRate)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TranscriptSegment[]))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext;
