namespace LocalWhisperMcp.TranscribeCpp;

/// <summary>Finds and repairs the stretches where Parakeet's decoder got stuck repeating itself.</summary>
/// <remarks>
/// Parakeet-ultra's model file gives the decoder a symbol budget per segment instead of per frame,
/// so a decoder that keeps predicting a duration of zero emits thousands of tokens in one instant
/// and drops the rest of the segment, while the run reports success. Speech reaches about 21
/// tokens in 2 seconds; the loops measured in the proof of concept reached more than 1,400. A
/// second run of the same audio, cut in two where the decoder got stuck, transcribes it normally.
/// The measurements are in <c>poc/TRANSCRIBE-CPP-RESULTS.md</c> on the
/// <c>poc/transcribe-cpp-evaluation</c> branch.
/// </remarks>
internal static class LoopGuard
{
    /// <summary>The number of tokens in <see cref="StuckWindowMs"/> that means the decoder is stuck.</summary>
    public const int StuckTokens = 60;

    /// <summary>The window for <see cref="StuckTokens"/>, in milliseconds.</summary>
    public const long StuckWindowMs = 2000;

    /// <summary>The shortest phrase, in words, whose repetition counts as a loop.</summary>
    public const int MinPhraseWords = 3;

    /// <summary>The number of times in a row a phrase must repeat to count as a loop.</summary>
    public const int MinPhraseRepeats = 8;

    /// <summary>The number of nested repairs before the guard keeps what it has.</summary>
    public const int MaxDepth = 3;

    // A repaired stretch starts this much before its segment, so the first word isn't clipped.
    private const long LeadMs = 300;

    // A piece shorter than this has no word to transcribe.
    private const long MinPieceMs = 100;

    private const int MaxPhraseWords = 10;

    /// <summary>Looks for a sign that the decoder got stuck.</summary>
    /// <param name="run">The output of one decode.</param>
    /// <returns>Where and why the decoder got stuck, or <see langword="null"/> if the output looks normal.</returns>
    /// <remarks>
    /// The signals are tried in order of how precisely they place the cut: the token rate first,
    /// then the library's own stop, then a repeated phrase. A phrase's first word can start a few
    /// tens of milliseconds before the stuck frame; on a 0 dB recording of the proof of concept, a
    /// cut there gave a repair that ended in invented words. So the phrase decides only when
    /// nothing else fires.
    /// </remarks>
    public static LoopSignal? Detect(DecodeRun run)
    {
        var tokens = run.TokenStartsMs;
        for (var i = 0; i + StuckTokens < tokens.Count; i++)
        {
            if (tokens[i + StuckTokens] - tokens[i] < StuckWindowMs)
            {
                return new LoopSignal(tokens[i], $"{StuckTokens} tokens in under {StuckWindowMs / 1000} s");
            }
        }

        if (run.StoppedEarly)
        {
            var at = tokens.Count > 0 ? tokens[^1] : run.Words.Count > 0 ? run.Words[^1].StartMs : 0;
            return new LoopSignal(at, "the library stopped the decode early");
        }

        return FindRepeatedPhrase(run.Words);
    }

    /// <summary>Transcribes a stretch of audio, transcribing again any part where the decoder got stuck.</summary>
    /// <param name="decode">Decodes the audio from a start to an end, in milliseconds, and returns times relative to that start.</param>
    /// <param name="durationMs">The length of the audio, in milliseconds.</param>
    /// <param name="onRepair">Receives each signal that started a repair.</param>
    /// <returns>The words with times from the start of the audio, each with a group that is unique per decoded segment.</returns>
    public static IReadOnlyList<TimedWord> Transcribe(Func<long, long, DecodeRun> decode, long durationMs, Action<LoopSignal>? onRepair = null)
    {
        var words = new List<TimedWord>();
        var groups = 0;
        Transcribe(decode, 0, durationMs, 0, words, ref groups, onRepair);
        return words;
    }

    private static void Transcribe(Func<long, long, DecodeRun> decode, long startMs, long endMs, int depth, List<TimedWord> output, ref int groups, Action<LoopSignal>? onRepair)
    {
        var run = decode(startMs, endMs).Shift(startMs);
        var signal = depth < MaxDepth ? Detect(run) : null;
        if (signal is null)
        {
            Append(run.Words, output, ref groups);
            return;
        }

        onRepair?.Invoke(signal);
        var at = Math.Clamp(signal.AtMs, startMs, endMs);
        var stuck = run.Segments.Count == 0 ? -1 : Math.Max(0, run.Segments.FindLastIndex(segment => segment.StartMs <= at));
        var pieceStart = stuck < 0 ? startMs : Math.Max(startMs, run.Segments[stuck].StartMs - LeadMs);

        // The decoder never reached the rest of the stuck segment, so its end comes from the next
        // segment or the end of the audio, not from the words.
        var pieceEnd = stuck >= 0 && stuck + 1 < run.Segments.Count ? run.Segments[stuck + 1].StartMs : endMs;

        Append(run.Words.Where(word => word.Group < stuck), output, ref groups);
        foreach (var (first, last) in new[] { (pieceStart, at), (at, pieceEnd) })
        {
            if (last - first >= MinPieceMs)
            {
                Transcribe(decode, first, last, depth + 1, output, ref groups, onRepair);
            }
        }

        if (stuck >= 0)
        {
            Append(run.Words.Where(word => word.Group > stuck), output, ref groups);
        }
    }

    private static void Append(IEnumerable<TimedWord> words, List<TimedWord> output, ref int groups)
    {
        var mapped = new Dictionary<int, int>();
        foreach (var word in words)
        {
            if (!mapped.TryGetValue(word.Group, out var group))
            {
                group = groups++;
                mapped[word.Group] = group;
            }

            output.Add(word with { Group = group });
        }
    }

    private static LoopSignal? FindRepeatedPhrase(IReadOnlyList<TimedWord> words)
    {
        var keys = words.Select(word => new string([.. word.Text.Where(char.IsLetterOrDigit)]).ToLowerInvariant()).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            for (var size = MinPhraseWords; size <= MaxPhraseWords && i + (size * MinPhraseRepeats) <= keys.Length; size++)
            {
                var repeats = 1;
                while (i + ((repeats + 1) * size) <= keys.Length && Same(keys, i, i + (repeats * size), size))
                {
                    repeats++;
                }

                if (repeats >= MinPhraseRepeats)
                {
                    return new LoopSignal(words[i].StartMs, $"\"{string.Join(' ', words.Skip(i).Take(size).Select(word => word.Text))}\" {repeats} times in a row");
                }
            }
        }

        return null;
    }

    private static bool Same(string[] keys, int first, int second, int size)
    {
        for (var k = 0; k < size; k++)
        {
            if (keys[first + k] != keys[second + k] || keys[first + k].Length == 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Represents one word of a transcript with its times.</summary>
/// <param name="Text">The word, with its punctuation.</param>
/// <param name="StartMs">The start, in milliseconds.</param>
/// <param name="EndMs">The end, in milliseconds.</param>
/// <param name="Group">The decoded segment the word belongs to; a new group starts a new transcript segment.</param>
internal readonly record struct TimedWord(string Text, long StartMs, long EndMs, int Group);

/// <summary>Represents a span of time.</summary>
/// <param name="StartMs">The start, in milliseconds.</param>
/// <param name="EndMs">The end, in milliseconds.</param>
internal readonly record struct TimedSpan(long StartMs, long EndMs);

/// <summary>Represents the output of one decode.</summary>
/// <param name="Words">The words, whose group is the index of their segment.</param>
/// <param name="TokenStartsMs">The start of each token, in milliseconds.</param>
/// <param name="Segments">The segments that the model cut the audio into.</param>
/// <param name="StoppedEarly">Whether the library stopped the decode before the end, because it was repeating itself or ran out of output budget.</param>
internal sealed record DecodeRun(IReadOnlyList<TimedWord> Words, IReadOnlyList<long> TokenStartsMs, List<TimedSpan> Segments, bool StoppedEarly)
{
    /// <summary>Moves every time by an offset.</summary>
    /// <param name="offsetMs">The offset, in milliseconds.</param>
    /// <returns>The same output with shifted times.</returns>
    public DecodeRun Shift(long offsetMs) => offsetMs == 0 ? this : new(
        [.. Words.Select(word => word with { StartMs = word.StartMs + offsetMs, EndMs = word.EndMs + offsetMs })],
        [.. TokenStartsMs.Select(start => start + offsetMs)],
        [.. Segments.Select(segment => new TimedSpan(segment.StartMs + offsetMs, segment.EndMs + offsetMs))],
        StoppedEarly);
}

/// <summary>Describes where and why the decoder got stuck.</summary>
/// <param name="AtMs">The time where it got stuck, in milliseconds.</param>
/// <param name="Reason">What showed it.</param>
internal sealed record LoopSignal(long AtMs, string Reason);
