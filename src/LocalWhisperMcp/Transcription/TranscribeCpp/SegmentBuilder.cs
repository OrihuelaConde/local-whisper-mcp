namespace LocalWhisperMcp.TranscribeCpp;

/// <summary>Groups timed words into transcript segments for the txt, srt, and json formats.</summary>
/// <remarks>
/// Parakeet cuts audio into segments of up to 30 seconds, which is too long for a subtitle. A
/// segment here ends at the end of a sentence, where the model's own segment ends, or before it
/// would pass <see cref="MaxDuration"/>.
/// </remarks>
internal static class SegmentBuilder
{
    /// <summary>The longest segment, unless one word alone is longer.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(15);

    private static readonly char[] Closers = ['"', '\'', ')', ']', '»', '”', '’'];

    /// <summary>Groups words into segments.</summary>
    /// <param name="words">The words in order, with times from the start of the audio.</param>
    /// <returns>The segments.</returns>
    public static IReadOnlyList<TranscriptSegment> Build(IReadOnlyList<TimedWord> words)
    {
        var segments = new List<TranscriptSegment>();
        var current = new List<TimedWord>();
        foreach (var word in words)
        {
            var text = word.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (current.Count > 0 && (word.Group != current[^1].Group || word.EndMs - current[0].StartMs > MaxDuration.TotalMilliseconds))
            {
                Flush();
            }

            current.Add(word with { Text = text });
            if (EndsSentence(text))
            {
                Flush();
            }
        }

        Flush();
        return segments;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            segments.Add(new TranscriptSegment(
                TimeSpan.FromMilliseconds(current[0].StartMs),
                TimeSpan.FromMilliseconds(current[^1].EndMs),
                string.Join(' ', current.Select(word => word.Text))));
            current.Clear();
        }
    }

    /// <summary>Checks whether a word ends a sentence.</summary>
    /// <param name="word">The word, with its punctuation.</param>
    /// <returns><see langword="true"/> if it ends with a period, a question mark, an exclamation mark, or an ellipsis, before any closing quote or bracket.</returns>
    internal static bool EndsSentence(string word) => word.TrimEnd(Closers) is { Length: > 0 } trimmed && trimmed[^1] is '.' or '?' or '!' or '…';
}
