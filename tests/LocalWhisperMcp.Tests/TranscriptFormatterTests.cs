namespace LocalWhisperMcp.Tests;

public sealed class TranscriptFormatterTests
{
    private static readonly TranscriptionResult Result = new(
        [
            new TranscriptSegment(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.25), "Hola, ¿cómo andás?"),
            new TranscriptSegment(TimeSpan.FromMilliseconds(3_661_004), TimeSpan.FromSeconds(3662.5), "Todo bien."),
        ],
        [new SpeechSpan(TimeSpan.Zero, TimeSpan.FromSeconds(3663))],
        TimeSpan.FromMilliseconds(3_663_456),
        "es");

    [Fact]
    public void Text_starts_a_paragraph_after_a_long_pause()
    {
        Assert.Equal("Hola, ¿cómo andás?\n\nTodo bien.", TranscriptFormatter.Format(Result, "txt"));
    }

    [Fact]
    public void Text_joins_segments_separated_by_short_pauses()
    {
        var result = new TranscriptionResult(
            [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Primera frase."),
                new TranscriptSegment(TimeSpan.FromSeconds(3.9), TimeSpan.FromSeconds(5), "Segunda frase."),
            ],
            [],
            TimeSpan.FromSeconds(5),
            "es");

        Assert.Equal("Primera frase. Segunda frase.", TranscriptFormatter.Format(result, "txt"));
    }

    [Fact]
    public void Text_marks_audio_without_speech()
    {
        var silent = new TranscriptionResult([], [], TimeSpan.FromSeconds(5), null);

        Assert.Equal(TranscriptFormatter.NoSpeech, TranscriptFormatter.Format(silent, "txt"));
    }

    [Fact]
    public void SubRip_numbers_the_cues_and_counts_hours()
    {
        Assert.Equal(
            "1\n00:00:00,500 --> 00:00:02,250\nHola, ¿cómo andás?\n\n2\n01:01:01,004 --> 01:01:02,500\nTodo bien.\n\n",
            TranscriptFormatter.Format(Result, "srt"));
    }

    [Fact]
    public void Json_uses_seconds_and_keeps_accents_readable()
    {
        Assert.Equal(
            """{"language":"es","duration":3663.46,"segments":[{"start":0.5,"end":2.25,"text":"Hola, ¿cómo andás?"},{"start":3661,"end":3662.5,"text":"Todo bien."}]}""",
            TranscriptFormatter.Format(Result, "json"));
    }

    [Fact]
    public void An_unknown_format_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => TranscriptFormatter.Format(Result, "vtt"));
    }
}
