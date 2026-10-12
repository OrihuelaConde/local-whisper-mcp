using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GemmaAsr;
using LiteRtLmSharp;
using LocalWhisperMcp;
using Microsoft.Extensions.Logging.Abstractions;

// Transcribes audio with Gemma 4 (through LiteRT-LM) or with Whisper, and reports timing and memory
// as JSON, so a script can run both engines on the same files and compare them. Each run loads one
// engine, which keeps the memory figures of the two apart. With --audio-dir, one model load
// transcribes every file of a folder, as a server that keeps the model in memory does.
var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

string[] files = options.AudioDir is null ? [options.Audio!] : [.. Directory.GetFiles(options.AudioDir).Order(StringComparer.Ordinal)];
var encoding = new UTF8Encoding(false);
double audioSeconds = 0;
double decodeSeconds = 0;
double transcribeSeconds = 0;
var words = 0;

using var output = new MemoryStream();
using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
json.WriteStartObject();
json.WriteString("engine", options.Engine);
json.WriteString("model", Path.GetFileName(options.Model));
json.WriteString("audio", Path.GetFileName(options.AudioDir ?? options.Audio));
json.WriteNumber("files", files.Length);

var stopwatch = Stopwatch.StartNew();
double loadSeconds;
if (options.Engine == "gemma")
{
    // Level 3 keeps errors and drops the per-call information the native library prints.
    LiteRtEngine.SetMinLogLevel(options.Verbose ? 0 : 3);
    using var transcriber = new GemmaTranscriber(options.Model, options.Backend == "gpu", options.AudioBackend == "gpu", options.Sampler == "greedy", options.MaxOutputTokens, options.ContextTokens, options.NoRepeatNgram, options.Activation == "f16");
    loadSeconds = stopwatch.Elapsed.TotalSeconds;

    var prompt = options.Prompt ?? GemmaTranscriber.BuildPrompt(options.Language);
    var clips = new List<GemmaClip>();
    await TranscribeFilesAsync(samples =>
    {
        var fileClips = transcriber.Transcribe(samples, prompt, options.ClipSeconds);
        clips.AddRange(fileClips);
        return Task.FromResult(string.Join(' ', fileClips.Select(clip => clip.Text).Where(text => text.Length > 0)));
    });

    json.WriteString("backend", options.Backend);
    json.WriteString("sampler", options.Sampler);
    json.WriteNumber("clipSeconds", options.ClipSeconds);
    json.WriteNumber("clipCount", clips.Count);
    json.WriteNumber("clipsAtOutputLimit", clips.Count(clip => clip.DecodeTokens >= options.MaxOutputTokens));

    // The per-clip detail of a whole folder would bury the totals.
    if (files.Length == 1)
    {
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
}
else
{
    NativeRuntime.Configure(options.Backend == "gpu" ? "auto" : "cpu");
    using var logs = NativeRuntime.ForwardLogs(NullLogger.Instance);
    using var transcriber = new Transcriber(options.Model, options.Threads);
    loadSeconds = stopwatch.Elapsed.TotalSeconds;

    var filesWithVad = 0;
    double speechSeconds = 0;
    var segments = 0;
    var languages = new SortedSet<string>(StringComparer.Ordinal);
    await TranscribeFilesAsync(async samples =>
    {
        // The same rule as the server: VAD for clean recordings, everything for noisy ones.
        var useVad = options.Vad == "on" || (options.Vad == "auto" && AudioQuality.MeasureSpreadDb(samples) >= AudioQuality.CleanSpreadDb);
        var result = await transcriber.TranscribeAsync(samples, options.Language, useVad ? options.VadModel : null, null, CancellationToken.None);
        filesWithVad += useVad ? 1 : 0;
        speechSeconds += result.Speech.Sum(span => (span.End - span.Start).TotalSeconds);
        segments += result.Segments.Count;
        languages.Add(result.Language ?? "none");
        return string.Join(' ', result.Segments.Select(segment => segment.Text).Where(text => text.Length > 0));
    });

    json.WriteString("backend", NativeRuntime.Device);
    json.WriteNumber("filesWithVad", filesWithVad);
    json.WriteNumber("speechSeconds", Math.Round(speechSeconds, 2));
    json.WriteNumber("segments", segments);
    json.WriteString("detectedLanguages", string.Join(',', languages));
}

using var self = Process.GetCurrentProcess();
json.WriteString("language", options.Language);
json.WriteNumber("audioSeconds", Math.Round(audioSeconds, 2));
json.WriteNumber("decodeSeconds", Math.Round(decodeSeconds, 3));
json.WriteNumber("loadSeconds", Math.Round(loadSeconds, 2));
json.WriteNumber("transcribeSeconds", Math.Round(transcribeSeconds, 2));
json.WriteNumber("secondsPerAudioMinute", Math.Round(transcribeSeconds / (audioSeconds / 60), 2));

// Processor time over elapsed time tells how many cores the run kept busy.
json.WriteNumber("processorSeconds", Math.Round(self.TotalProcessorTime.TotalSeconds, 1));
json.WriteNumber("peakWorkingSetMiB", self.PeakWorkingSet64 / (1024 * 1024));
json.WriteNumber("peakCommitMiB", self.PeakPagedMemorySize64 / (1024 * 1024));
json.WriteNumber("words", words);
json.WriteEndObject();
json.Flush();

Console.OutputEncoding = Encoding.UTF8;
Console.Out.WriteLine(Encoding.UTF8.GetString(output.ToArray()));

// The engine is disposed by now. Staying alive lets an outside tool check that its memory is free,
// which is what the server relies on to release an idle model.
Thread.Sleep(TimeSpan.FromSeconds(options.HoldSeconds));
return 0;

// Decodes each file, transcribes it, and adds its figures to the totals. Transcripts of private
// recordings go to files the caller chooses, never to the metrics.
async Task TranscribeFilesAsync(Func<float[], Task<string>> transcribe)
{
    foreach (var file in files)
    {
        stopwatch.Restart();
        var samples = await AudioDecoder.DecodeAsync(file, NullLogger.Instance, CancellationToken.None);
        if (options.LimitSeconds > 0)
        {
            samples = samples[..Math.Min(samples.Length, (int)(options.LimitSeconds * AudioDecoder.SampleRate))];
        }

        decodeSeconds += stopwatch.Elapsed.TotalSeconds;
        audioSeconds += samples.Length / (double)AudioDecoder.SampleRate;

        stopwatch.Restart();
        var transcript = await transcribe(samples);
        transcribeSeconds += stopwatch.Elapsed.TotalSeconds;
        words += transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var target = options.OutDir is null ? options.Out : Path.Combine(options.OutDir, Path.GetFileName(file) + ".txt");
        if (target is not null)
        {
            File.WriteAllText(target, transcript + "\n", encoding);
        }
    }
}

/// <summary>Holds the command-line options.</summary>
internal sealed record CliOptions(
    string Engine,
    string Model,
    string? Audio,
    string? AudioDir,
    string Backend,
    string AudioBackend,
    string Activation,
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
    string? OutDir,
    bool Verbose)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage =
        "Usage: gemma-asr --engine gemma|whisper --model MODEL (--audio AUDIO [--out TRANSCRIPT] | --audio-dir DIR --out-dir DIR)\n"
        + "  [--backend gpu|cpu] [--language auto|LANG] [--limit-seconds N] [--hold-seconds N] [--verbose true]\n"
        + "  gemma:   [--sampler greedy|default] [--clip-seconds 30] [--max-output-tokens 512] [--context-tokens 4096]\n"
        + "           [--no-repeat-ngram N] [--prompt TEXT] [--audio-backend cpu|gpu] [--activation f32|f16]\n"
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

        // A run takes one file, or a folder together with the folder for its transcripts.
        if (!values.TryGetValue("engine", out var engine) || engine is not ("gemma" or "whisper")
            || !values.TryGetValue("model", out var model)
            || values.ContainsKey("audio") == values.ContainsKey("audio-dir")
            || (values.ContainsKey("audio-dir") && !values.ContainsKey("out-dir")))
        {
            return null;
        }

        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local-whisper-mcp", "models");
        return new CliOptions(
            engine,
            model,
            values.GetValueOrDefault("audio"),
            values.GetValueOrDefault("audio-dir"),
            values.GetValueOrDefault("backend", "gpu").ToLowerInvariant(),
            values.GetValueOrDefault("audio-backend", "cpu").ToLowerInvariant(),
            values.GetValueOrDefault("activation", "f32").ToLowerInvariant(),
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
            values.GetValueOrDefault("out-dir"),
            bool.Parse(values.GetValueOrDefault("verbose", "false")));
    }
}
