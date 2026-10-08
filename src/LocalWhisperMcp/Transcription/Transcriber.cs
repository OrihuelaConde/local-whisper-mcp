using Whisper.net;

namespace LocalWhisperMcp;

/// <summary>Holds one loaded Whisper model and transcribes 16 kHz mono samples with it.</summary>
/// <remarks>
/// The model stays in memory, including GPU memory, until the instance is disposed. Calls aren't
/// thread-safe; callers serialize access.
/// </remarks>
internal sealed class Transcriber : IDisposable
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

    /// <summary>Checks whether Whisper knows a language.</summary>
    /// <param name="language">A language code such as <c>es</c>, or <c>auto</c>.</param>
    /// <returns><see langword="true"/> if the code is <c>auto</c> or a language that Whisper supports.</returns>
    public static bool IsSupportedLanguage(string language) =>
        language == "auto" || WhisperFactory.GetSupportedLanguages().Contains(language);

    /// <summary>Transcribes the samples, optionally only the spans where the VAD model detects speech.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="language">A language code, or <c>auto</c> to detect it.</param>
    /// <param name="vadModelPath">The path to the Silero VAD model, or <see langword="null"/> to transcribe everything.</param>
    /// <param name="progress">Receives the percentage of the speech transcribed so far.</param>
    /// <param name="cancellationToken">The token to cancel the transcription.</param>
    /// <returns>The transcription and the speech spans that were transcribed.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        float[] samples,
        string language,
        string? vadModelPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioDecoder.SampleRate);
        var spans = samples.Length == 0 ? []
            : vadModelPath is null ? [new SpeechSpan(TimeSpan.Zero, duration)]
            : DetectSpeech(samples, vadModelPath);
        if (spans.Length == 0)
        {
            return new TranscriptionResult([], [], duration, null);
        }

        var builder = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(threads);
        if (progress is not null)
        {
            builder = builder.WithProgressHandler(progress.Report);
        }

        using var processor = builder.Build();
        var map = SpeechMap.Join(samples, spans);
        var segments = new List<TranscriptSegment>();
        string? detected = null;
        await foreach (var result in processor.ProcessAsync(map.Samples, cancellationToken))
        {
            var (start, end) = map.ToSource(result.Start, result.End);
            detected ??= result.Language;
            segments.Add(new TranscriptSegment(start, end, result.Text.Trim()));
        }

        return new TranscriptionResult(segments, spans, duration, detected);
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

/// <summary>Joins the speech spans of an audio into one buffer and maps timestamps back to the original audio.</summary>
/// <remarks>
/// Whisper encodes audio in 30-second windows, so transcribing each span on its own costs a full
/// window per span. Joining the spans, with a short silence between them as whisper.cpp does,
/// keeps the cost proportional to the speech.
/// </remarks>
internal sealed class SpeechMap
{
    // The silence between joined spans, and the part of a span's end where a segment that runs into
    // the next span belongs to the next span.
    private const int Gap = AudioDecoder.SampleRate / 10;
    private const int Tail = AudioDecoder.SampleRate / 2;

    private readonly List<(int JoinedStart, int SourceStart, int Length)> pieces;

    private SpeechMap(float[] samples, List<(int JoinedStart, int SourceStart, int Length)> pieces)
    {
        Samples = samples;
        this.pieces = pieces;
    }

    /// <summary>Gets the joined samples.</summary>
    public float[] Samples { get; }

    /// <summary>Joins the speech spans of the samples.</summary>
    /// <param name="samples">The 16 kHz mono samples of the whole audio.</param>
    /// <param name="spans">The speech spans, in order.</param>
    /// <returns>The map.</returns>
    public static SpeechMap Join(float[] samples, IReadOnlyList<SpeechSpan> spans)
    {
        var pieces = new List<(int JoinedStart, int SourceStart, int Length)>();
        var joined = new List<float>(samples.Length);
        foreach (var span in spans)
        {
            var first = (int)Math.Clamp(span.Start.TotalSeconds * AudioDecoder.SampleRate, 0, samples.Length);
            var last = (int)Math.Clamp(span.End.TotalSeconds * AudioDecoder.SampleRate, first, samples.Length);
            pieces.Add((joined.Count, first, last - first));
            joined.AddRange(samples.AsSpan(first, last - first));
            joined.AddRange(new float[Gap]);
        }

        return new SpeechMap([.. joined], pieces);
    }

    /// <summary>Maps a segment of the joined samples to the original audio.</summary>
    /// <param name="start">The start of the segment in the joined samples.</param>
    /// <param name="end">The end of the segment in the joined samples.</param>
    /// <returns>The start and end in the original audio.</returns>
    public (TimeSpan Start, TimeSpan End) ToSource(TimeSpan start, TimeSpan end)
    {
        var startIndex = ToIndex(start);
        var endIndex = ToIndex(end);
        var startPiece = PieceAt(startIndex);

        // Smaller models often start a segment right where the previous one ended, inside the tail
        // of the previous span. A segment that starts in that tail and ends in a later span belongs
        // to the later span.
        if (PieceAt(endIndex) > startPiece && startIndex >= pieces[startPiece].JoinedStart + pieces[startPiece].Length + Gap - Tail)
        {
            startIndex = pieces[++startPiece].JoinedStart;
        }

        return (ToSourceTime(startIndex), ToSourceTime(endIndex));
    }

    private static int ToIndex(TimeSpan time) => (int)(time.TotalSeconds * AudioDecoder.SampleRate);

    private int PieceAt(int index) => Math.Max(0, pieces.FindLastIndex(p => p.JoinedStart <= index));

    private TimeSpan ToSourceTime(int index)
    {
        var piece = pieces[PieceAt(index)];
        var offset = Math.Clamp(index - piece.JoinedStart, 0, piece.Length);
        return TimeSpan.FromSeconds((piece.SourceStart + offset) / (double)AudioDecoder.SampleRate);
    }
}

/// <summary>Represents a span of audio that contains speech.</summary>
/// <param name="Start">The start of the span.</param>
/// <param name="End">The end of the span.</param>
internal sealed record SpeechSpan(TimeSpan Start, TimeSpan End);

/// <summary>Represents one transcribed segment with timestamps from the beginning of the audio.</summary>
/// <param name="Start">The start of the segment.</param>
/// <param name="End">The end of the segment.</param>
/// <param name="Text">The transcribed text.</param>
internal sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Represents the outcome of one transcription.</summary>
/// <param name="Segments">The transcribed segments.</param>
/// <param name="Speech">The spans that were transcribed.</param>
/// <param name="Duration">The duration of the whole audio.</param>
/// <param name="Language">The language Whisper used, or <see langword="null"/> if there was no speech.</param>
internal sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<SpeechSpan> Speech, TimeSpan Duration, string? Language);
