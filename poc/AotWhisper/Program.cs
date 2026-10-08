using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PocCore;
using Whisper.net;
using Whisper.net.Logger;

// Transcribes one audio file and reports timing and memory on stderr, so stdout carries only
// the transcript.
var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

Transcriber.ConfigureRuntime(options.Runtime);
if (options.Verbose)
{
    LogProvider.AddLogger((level, message) => Console.Error.Write($"[whisper {level}] {message}"));
}

var total = Stopwatch.StartNew();
Metrics.Log($"decoder: {AudioLoader.GetDecoderName(options.Audio)}");
var samples = await AudioLoader.LoadAsync(options.Audio);
var decodeTime = total.Elapsed;
Metrics.Log($"decoded {samples.Length / (double)AudioLoader.SampleRate:F1} s in {decodeTime.TotalSeconds:F2} s");

// --dump writes the decoded samples as a 16 kHz WAV file, to compare decoders without a model.
if (options.Dump is not null)
{
    WaveWriter.Write(options.Dump, samples, AudioLoader.SampleRate);
    return 0;
}

var stopwatch = Stopwatch.StartNew();
using var transcriber = new Transcriber(options.Model, options.Threads);
var loadTime = stopwatch.Elapsed;
Metrics.Log($"runtime library: {Transcriber.LoadedRuntime}");
Metrics.Log($"native info: {WhisperFactory.GetRuntimeInfo()}");
Metrics.Log($"dynamic code: {RuntimeFeature.IsDynamicCodeSupported}");

stopwatch.Restart();
var result = await transcriber.TranscribeAsync(samples, options.Language, options.VadModel);
var transcribeTime = stopwatch.Elapsed;

Console.OutputEncoding = Encoding.UTF8;
Console.Out.WriteLine(TranscriptFormatter.Format(result.Segments, options.Format));

using var self = Process.GetCurrentProcess();
var minutes = result.Duration.TotalMinutes;
if (options.VadModel is not null)
{
    Metrics.Log($"vad: {result.Speech.Count} speech spans, {result.SpeechDuration.TotalSeconds:F1} s of {result.Duration.TotalSeconds:F1} s");
}

Metrics.Log($"model load: {loadTime.TotalSeconds:F2} s, transcribe (with vad): {transcribeTime.TotalSeconds:F2} s");
Metrics.Log($"seconds per audio minute: {transcribeTime.TotalSeconds / minutes:F2} (transcribe), {(loadTime + transcribeTime).TotalSeconds / minutes:F2} (with load)");
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
    string? Dump,
    bool Verbose)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage =
        "Usage: AotWhisper --audio AUDIO (--model MODEL | --dump OUTPUT_WAV) [--runtime auto|cpu|vulkan|cuda] "
        + "[--language auto|LANG] [--vad VAD_MODEL] [--threads N] [--format txt|srt|json] [--verbose true]";

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
            dump,
            bool.Parse(values.GetValueOrDefault("verbose", "false")));
    }
}

/// <summary>Writes measurement lines to stderr.</summary>
internal static class Metrics
{
    /// <summary>Writes one measurement line.</summary>
    /// <param name="message">The measurement to write.</param>
    public static void Log(string message) => Console.Error.WriteLine($"[metrics] {message}");
}
