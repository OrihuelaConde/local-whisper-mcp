using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalWhisperMcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HintsEval;

/// <summary>Transcribes every recording in a folder with the server's code and an optional prompt per recording.</summary>
/// <remarks>
/// Usage: <c>hints-eval --model MODEL_FILE --audio-dir DIR --out-dir DIR [--language CODE]
/// [--prompts PROMPTS_TSV | --prompt TEXT] [--carry] [--vad auto|on|off]</c>.
/// PROMPTS_TSV has one "file name, tab, prompt" line per recording; recordings it doesn't list
/// get no prompt. The VAD decision, the VAD model, the thread count, and the native runtime are the
/// server's. Each transcript goes to OUT_DIR/FILE_NAME.txt in the txt format, and the program
/// prints the timings as one JSON object.
/// </remarks>
internal static class HintsEvalProgram
{
    private static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>();
        var carry = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--carry")
            {
                carry = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                options[args[i][2..]] = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unexpected argument '{args[i]}'.");
                return 2;
            }
        }

        if (!options.TryGetValue("model", out var modelPath) || !options.TryGetValue("audio-dir", out var audioDir) || !options.TryGetValue("out-dir", out var outDir))
        {
            Console.Error.WriteLine("Usage: hints-eval --model MODEL_FILE --audio-dir DIR --out-dir DIR [--language CODE] [--prompts PROMPTS_TSV | --prompt TEXT] [--carry] [--vad auto|on|off]");
            return 2;
        }

        var language = options.GetValueOrDefault("language", "auto");
        var vad = options.GetValueOrDefault("vad", "auto");
        if (vad is not ("auto" or "on" or "off"))
        {
            Console.Error.WriteLine($"--vad takes auto, on, or off, not {vad}.");
            return 2;
        }

        var sharedPrompt = options.GetValueOrDefault("prompt");
        var prompts = options.TryGetValue("prompts", out var promptsPath)
            ? File.ReadLines(promptsPath, Encoding.UTF8)
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2 && parts[1].Trim().Length > 0)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim())
            : new Dictionary<string, string>();

        var settings = ServerSettings.FromEnvironment([], []);
        NativeRuntime.Configure(settings.Runtime);

        // whisper.cpp names the GPU, and warns about a prompt that's too long, only in its log.
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
        using var logs = NativeRuntime.ForwardLogs(loggerFactory.CreateLogger("whisper.cpp"));
        var vadPath = Path.Combine(settings.ModelsDirectory, ModelCatalog.VadModelFileName);
        if (vad != "off" && !File.Exists(vadPath))
        {
            Console.Error.WriteLine($"The VAD model isn't at {vadPath}. Run a transcription with the server once, or pass --vad off.");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var load = Stopwatch.StartNew();
        using var transcriber = new Transcriber(modelPath, settings.Threads);
        load.Stop();
        if (!Transcriber.IsSupportedLanguage(language))
        {
            Console.Error.WriteLine($"Whisper doesn't support the language '{language}'.");
            return 2;
        }

        var files = Directory.GetFiles(audioDir).Where(file => AudioDecoder.DetectFormat(file) != AudioFormat.Other).Order(StringComparer.Ordinal).ToList();
        double audioSeconds = 0, speechSeconds = 0, transcribeSeconds = 0;
        int prompted = 0, vadOn = 0, words = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var samples = await AudioDecoder.DecodeAsync(file, NullLogger.Instance, CancellationToken.None);
            var useVad = vad switch
            {
                "on" => true,
                "off" => false,
                _ => AudioQuality.MeasureSpreadDb(samples) >= AudioQuality.CleanSpreadDb,
            };
            var prompt = prompts.GetValueOrDefault(name) ?? sharedPrompt;

            var watch = Stopwatch.StartNew();
            var result = await transcriber.TranscribeAsync(samples, language, useVad ? vadPath : null, prompt, carry, null, CancellationToken.None);
            watch.Stop();

            var text = TranscriptFormatter.Format(result, "txt");
            await File.WriteAllTextAsync(Path.Combine(outDir, name + ".txt"), text, new UTF8Encoding(false));
            audioSeconds += result.Duration.TotalSeconds;
            speechSeconds += result.Speech.Sum(span => (span.End - span.Start).TotalSeconds);
            transcribeSeconds += watch.Elapsed.TotalSeconds;
            prompted += prompt is null ? 0 : 1;
            vadOn += useVad ? 1 : 0;
            words += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        var timings = new Dictionary<string, object?>
        {
            ["files"] = files.Count,
            ["prompted"] = prompted,
            ["carry"] = carry,
            ["vadOn"] = vadOn,
            ["audioSeconds"] = Math.Round(audioSeconds, 1),
            ["speechSeconds"] = Math.Round(speechSeconds, 1),
            ["loadSeconds"] = Math.Round(load.Elapsed.TotalSeconds, 2),
            ["transcribeSeconds"] = Math.Round(transcribeSeconds, 2),
            ["secondsPerAudioMinute"] = audioSeconds > 0 ? Math.Round(transcribeSeconds / audioSeconds * 60, 2) : 0,
            ["words"] = words,
            ["device"] = NativeRuntime.Device,
        };
        Console.WriteLine(JsonSerializer.Serialize(timings));
        return 0;
    }
}
