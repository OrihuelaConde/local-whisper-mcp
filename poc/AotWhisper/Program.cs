using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.net;
using Whisper.net.LibraryLoader;

// Transcribes one audio file and reports timing and memory on stderr, so stdout carries only
// the transcript.
var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

RuntimeOptions.RuntimeLibraryOrder = options.Runtime switch
{
    "cpu" => [RuntimeLibrary.Cpu],
    "vulkan" => [RuntimeLibrary.Vulkan],
    "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
    _ => RuntimeOptions.RuntimeLibraryOrder,
};

var total = Stopwatch.StartNew();
var samples = await AudioLoader.LoadAsync(options.Audio);
var audioDuration = TimeSpan.FromSeconds(samples.Length / (double)AudioLoader.SampleRate);
var decodeTime = total.Elapsed;
Metrics.Log($"decoded {audioDuration.TotalSeconds:F1} s in {decodeTime.TotalSeconds:F2} s");

// --dump writes the decoded samples as a 16 kHz WAV file, to compare decoders without a model.
if (options.Dump is not null)
{
    WaveWriter.Write(options.Dump, samples, AudioLoader.SampleRate);
    return 0;
}

var stopwatch = Stopwatch.StartNew();
using var factory = WhisperFactory.FromPath(options.Model);
using var processor = factory.CreateBuilder()
    .WithLanguage(options.Language)
    .WithThreads(options.Threads)
    .Build();
var loadTime = stopwatch.Elapsed;

Metrics.Log($"runtime library: {RuntimeOptions.LoadedLibrary}");
Metrics.Log($"native info: {WhisperFactory.GetRuntimeInfo()}");
Metrics.Log($"dynamic code: {RuntimeFeature.IsDynamicCodeSupported}");

var speech = new List<(TimeSpan Start, TimeSpan End)>();
TimeSpan vadTime = default;
if (options.VadModel is not null)
{
    stopwatch.Restart();
    using var vadFactory = WhisperVadFactory.FromPath(options.VadModel);
    using var vad = vadFactory.CreateBuilder().WithThreads(options.Threads).Build();
    foreach (var segment in vad.DetectSpeech(samples))
    {
        speech.Add((segment.Start, segment.End));
    }

    vadTime = stopwatch.Elapsed;
    var speechDuration = TimeSpan.FromTicks(speech.Sum(s => (s.End - s.Start).Ticks));
    Metrics.Log($"vad: {speech.Count} speech spans, {speechDuration.TotalSeconds:F1} s of {audioDuration.TotalSeconds:F1} s");
}
else
{
    speech.Add((TimeSpan.Zero, audioDuration));
}

stopwatch.Restart();
var segments = new List<TranscriptSegment>();
foreach (var (start, end) in speech)
{
    var first = (int)Math.Min(samples.Length, start.TotalSeconds * AudioLoader.SampleRate);
    var last = (int)Math.Min(samples.Length, end.TotalSeconds * AudioLoader.SampleRate);
    await foreach (var result in processor.ProcessAsync(samples.AsMemory(first, last - first)))
    {
        segments.Add(new TranscriptSegment(start + result.Start, start + result.End, result.Text.Trim(), result.Language));
    }
}

var transcribeTime = stopwatch.Elapsed;

Console.OutputEncoding = Encoding.UTF8;
Console.Out.Write(TranscriptFormatter.Format(segments, options.Format));

using var self = Process.GetCurrentProcess();
var minutes = audioDuration.TotalMinutes;
Metrics.Log($"audio: {audioDuration.TotalSeconds:F1} s, decode {decodeTime.TotalSeconds:F2} s");
Metrics.Log($"model load: {loadTime.TotalSeconds:F2} s, vad: {vadTime.TotalSeconds:F2} s, transcribe: {transcribeTime.TotalSeconds:F2} s");
Metrics.Log($"seconds per audio minute: {transcribeTime.TotalSeconds / minutes:F2} (transcribe only), {(loadTime + vadTime + transcribeTime).TotalSeconds / minutes:F2} (with load)");
Metrics.Log($"peak working set: {self.PeakWorkingSet64 / (1024.0 * 1024.0):F0} MiB");
Metrics.Log($"total: {total.Elapsed.TotalSeconds:F2} s");
return 0;

/// <summary>Holds the command-line options of the proof of concept.</summary>
internal sealed record CliOptions(
    string Model,
    string Audio,
    string Runtime,
    string Language,
    string? VadModel,
    int Threads,
    string Format,
    string? Dump)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage =
        "Usage: AotWhisper --audio AUDIO (--model MODEL | --dump OUTPUT_WAV) [--runtime auto|cpu|vulkan|cuda] "
        + "[--language auto|LANG] [--vad VAD_MODEL] [--threads N] [--format txt|srt|json]";

    /// <summary>Parses the command-line arguments.</summary>
    /// <param name="args">The raw arguments.</param>
    /// <returns>The parsed options, or <see langword="null"/> if a required option is missing.</returns>
    public static CliOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i].TrimStart('-')] = args[i + 1];
        }

        var dump = values.GetValueOrDefault("dump");
        if (!values.TryGetValue("audio", out var audio) || (!values.ContainsKey("model") && dump is null))
        {
            return null;
        }

        return new CliOptions(
            values.GetValueOrDefault("model", string.Empty),
            audio,
            values.GetValueOrDefault("runtime", "auto").ToLowerInvariant(),
            values.GetValueOrDefault("language", "auto"),
            values.GetValueOrDefault("vad"),
            int.Parse(values.GetValueOrDefault("threads", Math.Min(Environment.ProcessorCount, 8).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("format", "txt").ToLowerInvariant(),
            dump);
    }
}

/// <summary>Represents one transcribed segment with absolute timestamps.</summary>
/// <param name="Start">The start of the segment, from the beginning of the audio.</param>
/// <param name="End">The end of the segment, from the beginning of the audio.</param>
/// <param name="Text">The transcribed text.</param>
/// <param name="Language">The detected language code.</param>
internal sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, string Language);

/// <summary>Formats a transcript as plain text, SubRip, or JSON.</summary>
internal static class TranscriptFormatter
{
    /// <summary>Formats the segments in the requested format.</summary>
    /// <param name="segments">The transcribed segments.</param>
    /// <param name="format">One of <c>txt</c>, <c>srt</c>, or <c>json</c>.</param>
    /// <returns>The formatted transcript.</returns>
    public static string Format(IReadOnlyList<TranscriptSegment> segments, string format) => format switch
    {
        "srt" => ToSrt(segments),
        "json" => JsonSerializer.Serialize(segments.ToArray(), TranscriptJsonContext.Default.TranscriptSegmentArray) + Environment.NewLine,
        _ => string.Join(' ', segments.Select(s => s.Text)) + Environment.NewLine,
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
internal static class WaveWriter
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

/// <summary>Writes measurement lines to stderr.</summary>
internal static class Metrics
{
    /// <summary>Writes one measurement line.</summary>
    /// <param name="message">The measurement to write.</param>
    public static void Log(string message) => Console.Error.WriteLine($"[metrics] {message}");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TranscriptSegment[]))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext;
