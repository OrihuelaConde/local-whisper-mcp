using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GemmaAsr;
using LiteRtLmSharp;
using LocalWhisperMcp;
using Microsoft.Extensions.Logging.Abstractions;

// Transcribes one audio file with Gemma 4 (through LiteRT-LM) or with Whisper, and reports timing
// and memory as JSON, so a script can run both engines on the same files and compare them. Each
// run loads one engine, which keeps the memory figures of the two apart.
var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

var stopwatch = Stopwatch.StartNew();
var samples = await AudioDecoder.DecodeAsync(options.Audio, NullLogger.Instance, CancellationToken.None);
if (options.LimitSeconds > 0)
{
    samples = samples[..Math.Min(samples.Length, (int)(options.LimitSeconds * AudioDecoder.SampleRate))];
}

var decodeSeconds = stopwatch.Elapsed.TotalSeconds;
var audioSeconds = samples.Length / (double)AudioDecoder.SampleRate;

using var output = new MemoryStream();
using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
json.WriteStartObject();
json.WriteString("engine", options.Engine);
json.WriteString("model", Path.GetFileName(options.Model));
json.WriteString("audio", Path.GetFileName(options.Audio));
json.WriteNumber("audioSeconds", Math.Round(audioSeconds, 2));
json.WriteNumber("decodeSeconds", Math.Round(decodeSeconds, 3));

string transcript;
double loadSeconds;
double transcribeSeconds;
if (options.Engine == "gemma")
{
    // Level 3 keeps errors and drops the per-call information the native library prints.
    LiteRtEngine.SetMinLogLevel(options.Verbose ? 0 : 3);
    stopwatch.Restart();
    using var transcriber = new GemmaTranscriber(options.Model, options.Backend == "gpu", options.Sampler == "greedy", options.MaxOutputTokens, options.ContextTokens, options.NoRepeatNgram);
    loadSeconds = stopwatch.Elapsed.TotalSeconds;

    stopwatch.Restart();
    var clips = transcriber.Transcribe(samples, options.Prompt ?? GemmaTranscriber.BuildPrompt(options.Language), options.ClipSeconds);
    transcribeSeconds = stopwatch.Elapsed.TotalSeconds;
    transcript = string.Join(' ', clips.Select(clip => clip.Text).Where(text => text.Length > 0));

    json.WriteString("backend", options.Backend);
    json.WriteString("sampler", options.Sampler);
    json.WriteNumber("clipSeconds", options.ClipSeconds);
    json.WriteStartArray("clips");
    foreach (var clip in clips)
    {
        json.WriteStartObject();
        json.WriteNumber("start", Math.Round(clip.Start, 2));
        json.WriteNumber("duration", Math.Round(clip.Duration, 2));
        json.WriteNumber("seconds", Math.Round(clip.Seconds, 3));
        json.WriteNumber("prefillTokens", clip.PrefillTokens);
        json.WriteNumber("decodeTokens", clip.DecodeTokens);
        json.WriteNumber("prefillTokensPerSecond", Math.Round(clip.PrefillTokensPerSecond, 1));
        json.WriteNumber("decodeTokensPerSecond", Math.Round(clip.DecodeTokensPerSecond, 1));
        json.WriteNumber("timeToFirstToken", Math.Round(clip.TimeToFirstToken, 3));
        json.WriteNumber("characters", clip.Text.Length);
        json.WriteEndObject();
    }

    json.WriteEndArray();
}
else
{
    NativeRuntime.Configure(options.Backend == "gpu" ? "auto" : "cpu");
    using var logs = NativeRuntime.ForwardLogs(NullLogger.Instance);
    stopwatch.Restart();
    using var transcriber = new Transcriber(options.Model, options.Threads);
    loadSeconds = stopwatch.Elapsed.TotalSeconds;

    // The same rule as the server: VAD for clean recordings, everything for noisy ones.
    var spread = AudioQuality.MeasureSpreadDb(samples);
    var useVad = options.Vad == "on" || (options.Vad == "auto" && spread >= AudioQuality.CleanSpreadDb);
    stopwatch.Restart();
    var result = await transcriber.TranscribeAsync(samples, options.Language, useVad ? options.VadModel : null, null, CancellationToken.None);
    transcribeSeconds = stopwatch.Elapsed.TotalSeconds;
    transcript = string.Join(' ', result.Segments.Select(segment => segment.Text).Where(text => text.Length > 0));

    json.WriteString("backend", NativeRuntime.Device);
    json.WriteBoolean("vad", useVad);
    json.WriteNumber("levelSpreadDb", Math.Round(spread, 1));
    json.WriteNumber("speechSeconds", Math.Round(result.Speech.Sum(span => (span.End - span.Start).TotalSeconds), 2));
    json.WriteNumber("segments", result.Segments.Count);
    json.WriteString("detectedLanguage", result.Language);
}

using var self = Process.GetCurrentProcess();
json.WriteString("language", options.Language);
json.WriteNumber("loadSeconds", Math.Round(loadSeconds, 2));
json.WriteNumber("transcribeSeconds", Math.Round(transcribeSeconds, 2));
json.WriteNumber("secondsPerAudioMinute", Math.Round(transcribeSeconds / (audioSeconds / 60), 2));

// Processor time over elapsed time tells how many cores the run kept busy.
json.WriteNumber("processorSeconds", Math.Round(self.TotalProcessorTime.TotalSeconds, 1));
json.WriteNumber("peakWorkingSetMiB", self.PeakWorkingSet64 / (1024 * 1024));
json.WriteNumber("peakCommitMiB", self.PeakPagedMemorySize64 / (1024 * 1024));
json.WriteNumber("words", transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
json.WriteEndObject();
json.Flush();

// Transcripts of private recordings go to a file the caller chooses, never to the metrics.
if (options.Out is not null)
{
    File.WriteAllText(options.Out, transcript + "\n", new UTF8Encoding(false));
}

Console.OutputEncoding = Encoding.UTF8;
Console.Out.WriteLine(Encoding.UTF8.GetString(output.ToArray()));

// The engine is disposed by now. Staying alive lets an outside tool check that its memory is free,
// which is what the server relies on to release an idle model.
Thread.Sleep(TimeSpan.FromSeconds(options.HoldSeconds));
return 0;

/// <summary>Holds the command-line options.</summary>
internal sealed record CliOptions(
    string Engine,
    string Model,
    string Audio,
    string Backend,
    string Language,
    string Vad,
    string VadModel,
    int Threads,
    string Sampler,
    double ClipSeconds,
    int MaxOutputTokens,
    int ContextTokens,
    int NoRepeatNgram,
    string? Prompt,
    double LimitSeconds,
    double HoldSeconds,
    string? Out,
    bool Verbose)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage =
        "Usage: gemma-asr --engine gemma|whisper --model MODEL --audio AUDIO [--backend gpu|cpu] [--language auto|LANG] "
        + "[--out TRANSCRIPT] [--limit-seconds N] [--hold-seconds N] [--verbose true]\n"
        + "  gemma:   [--sampler greedy|default] [--clip-seconds 30] [--max-output-tokens 512] [--context-tokens 4096]\n"
        + "           [--no-repeat-ngram N] [--prompt TEXT]\n"
        + "  whisper: [--vad auto|on|off] [--vad-model VAD_MODEL] [--threads N]";

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

        if (!values.TryGetValue("engine", out var engine) || engine is not ("gemma" or "whisper")
            || !values.TryGetValue("model", out var model) || !values.TryGetValue("audio", out var audio))
        {
            return null;
        }

        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local-whisper-mcp", "models");
        return new CliOptions(
            engine,
            model,
            audio,
            values.GetValueOrDefault("backend", "gpu").ToLowerInvariant(),
            values.GetValueOrDefault("language", "auto").ToLowerInvariant(),
            values.GetValueOrDefault("vad", "auto").ToLowerInvariant(),
            values.GetValueOrDefault("vad-model", Path.Combine(models, "ggml-silero-v6.2.0.bin")),
            int.Parse(values.GetValueOrDefault("threads", Math.Min(Environment.ProcessorCount, 8).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("sampler", "greedy").ToLowerInvariant(),
            double.Parse(values.GetValueOrDefault("clip-seconds", "30"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("max-output-tokens", "512"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("context-tokens", "4096"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("no-repeat-ngram", "0"), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("prompt"),
            double.Parse(values.GetValueOrDefault("limit-seconds", "0"), CultureInfo.InvariantCulture),
            double.Parse(values.GetValueOrDefault("hold-seconds", "0"), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("out"),
            bool.Parse(values.GetValueOrDefault("verbose", "false")));
    }
}
