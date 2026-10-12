using System.Diagnostics;
using LiteRtLmSharp;
using LocalWhisperMcp;

namespace GemmaAsr;

/// <summary>Holds one loaded Gemma 4 model and transcribes 16 kHz mono samples with it.</summary>
/// <remarks>
/// Gemma 4 is a chat model that takes audio as an attachment, so a transcription is a prompt plus
/// one clip of up to 30 seconds, and the answer carries no timestamps. Longer audio goes through as
/// consecutive clips, each in a new conversation.
/// </remarks>
internal sealed class GemmaTranscriber : IDisposable
{
    // The prompts are the ones the Gemma 4 model card gives for speech recognition.
    private const string Instructions =
        "Follow these specific instructions for formatting the answer:\n" +
        "* Only output the transcription, with no newlines.\n" +
        "* When transcribing numbers, write the digits, i.e. write 1.7 and not one point seven, and write 3 instead of three.";

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["de"] = "German",
        ["en"] = "English",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["it"] = "Italian",
        ["pt"] = "Portuguese",
    };

    private readonly LiteRtEngine engine;
    private readonly LiteRtConversationOptions conversationOptions;
    private readonly LiteRtSendOptions? sendOptions;

    /// <summary>Initializes a new instance of the <see cref="GemmaTranscriber"/> class and loads the model.</summary>
    /// <param name="modelPath">The path to the <c>.litertlm</c> model file.</param>
    /// <param name="useGpu">Whether the language model runs on the GPU. The audio encoder always runs on the CPU.</param>
    /// <param name="greedy">Whether to pick the most likely token at each step instead of sampling.</param>
    /// <param name="maxOutputTokens">The longest answer for one clip, in tokens.</param>
    /// <param name="contextTokens">The context window, which has to hold the prompt, one clip, and its answer.</param>
    /// <param name="noRepeatNgram">The length, in tokens, of the sequences an answer may not repeat, or 0 for no limit.</param>
    public GemmaTranscriber(string modelPath, bool useGpu, bool greedy, int maxOutputTokens, int contextTokens, int noRepeatNgram)
    {
        // On noise, the model can repeat one word until it reaches the output limit.
        sendOptions = noRepeatNgram > 0
            ? new LiteRtSendOptions { NoRepeatNgram = new LiteRtNoRepeatNgramOptions { NgramSize = noRepeatNgram } }
            : null;
        engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = useGpu ? LiteRtBackend.Gpu : LiteRtBackend.Cpu,

            // The audio sub-model of Gemma 4 declares the CPU as its only backend.
            AudioBackend = LiteRtBackend.Cpu,

            // Audio takes 25 tokens per second, so a 30-second clip is about 750 tokens.
            MaxNumTokens = contextTokens,
            EnableBenchmark = true,
        });
        conversationOptions = new LiteRtConversationOptions
        {
            MaxOutputTokens = maxOutputTokens,
            // A top-k cut of 1 is greedy decoding. On the CPU backend, LiteRT-LM v0.18.0 implements
            // only the top-p sampler type; the greedy and top-k types fail with "not implemented yet".
            Sampler = greedy ? new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopP, TopK = 1 } : null,
        };
    }

    /// <summary>Builds the transcription prompt for a language.</summary>
    /// <param name="language">A language code such as <c>es</c>, or <c>auto</c>.</param>
    /// <returns>The prompt.</returns>
    public static string BuildPrompt(string language) =>
        LanguageNames.TryGetValue(language, out var name)
            ? $"Transcribe the following speech segment in {name} into {name} text.\n\n{Instructions}"
            : $"Transcribe the following speech segment in its original language. {Instructions}";

    /// <summary>Transcribes the samples as consecutive clips.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="prompt">The instruction that goes before each clip.</param>
    /// <param name="maxClipSeconds">The longest clip, in seconds.</param>
    /// <returns>One result per clip, in order.</returns>
    public IReadOnlyList<GemmaClip> Transcribe(float[] samples, string prompt, double maxClipSeconds)
    {
        var clips = new List<GemmaClip>();
        foreach (var (start, length) in AudioChunker.Split(samples, maxClipSeconds, searchSeconds: 5))
        {
            var wave = WaveBytes.Encode(samples.AsSpan(start, length), AudioDecoder.SampleRate);
            var stopwatch = Stopwatch.StartNew();
            using var conversation = engine.CreateConversation(conversationOptions);
            var response = conversation.Send(prompt, [LiteRtAttachment.Audio(wave)], sendOptions);
            var elapsed = stopwatch.Elapsed;
            var benchmark = conversation.GetBenchmarkInfo();
            clips.Add(new GemmaClip(
                start / (double)AudioDecoder.SampleRate,
                length / (double)AudioDecoder.SampleRate,
                (response.Text ?? string.Empty).Trim(),
                elapsed.TotalSeconds,
                benchmark?.LastPrefillTokenCount ?? 0,
                benchmark?.LastDecodeTokenCount ?? 0,
                benchmark?.LastPrefillTokensPerSecond ?? 0,
                benchmark?.LastDecodeTokensPerSecond ?? 0,
                benchmark?.TimeToFirstTokenSeconds ?? 0));
        }

        return clips;
    }

    /// <inheritdoc/>
    public void Dispose() => engine.Dispose();
}

/// <summary>Represents the transcription of one clip.</summary>
/// <param name="Start">The start of the clip, in seconds from the beginning of the audio.</param>
/// <param name="Duration">The length of the clip, in seconds.</param>
/// <param name="Text">The transcribed text.</param>
/// <param name="Seconds">The time the clip took, from sending it to receiving the whole answer.</param>
/// <param name="PrefillTokens">The tokens of the prompt and the audio.</param>
/// <param name="DecodeTokens">The tokens of the answer.</param>
/// <param name="PrefillTokensPerSecond">The speed at which the model read the prompt and the audio.</param>
/// <param name="DecodeTokensPerSecond">The speed at which the model wrote the answer.</param>
/// <param name="TimeToFirstToken">The seconds from the start of the prefill to the first token of the answer.</param>
internal sealed record GemmaClip(
    double Start,
    double Duration,
    string Text,
    double Seconds,
    int PrefillTokens,
    int DecodeTokens,
    double PrefillTokensPerSecond,
    double DecodeTokensPerSecond,
    double TimeToFirstToken);
