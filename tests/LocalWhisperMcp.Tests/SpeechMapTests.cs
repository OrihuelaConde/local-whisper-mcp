namespace LocalWhisperMcp.Tests;

public sealed class SpeechMapTests
{
    private const int Rate = AudioDecoder.SampleRate;

    // Speech from 2 to 4 s and from 10 to 11 s of a 12-second audio.
    private static readonly SpeechMap Map = SpeechMap.Join(
        new float[12 * Rate],
        [new SpeechSpan(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)), new SpeechSpan(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11))]);

    [Fact]
    public void Joined_audio_holds_the_spans_with_a_short_gap_after_each()
    {
        Assert.Equal((3 * Rate) + (2 * (Rate / 10)), Map.Samples.Length);
    }

    [Fact]
    public void Timestamps_map_back_to_the_original_audio()
    {
        // The second span starts at 2.1 s in the joined audio: 2 s of speech plus the 0.1 s gap.
        var first = Map.ToSource(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
        var second = Map.ToSource(TimeSpan.FromSeconds(2.3), TimeSpan.FromSeconds(2.8));

        Assert.Equal((TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3.5)), first);
        Assert.Equal((TimeSpan.FromSeconds(10.2), TimeSpan.FromSeconds(10.7)), second);
    }

    [Fact]
    public void A_segment_that_starts_in_the_tail_of_a_span_and_ends_in_the_next_belongs_to_the_next()
    {
        var (start, end) = Map.ToSource(TimeSpan.FromSeconds(1.8), TimeSpan.FromSeconds(2.6));

        Assert.Equal(TimeSpan.FromSeconds(10), start);
        Assert.Equal(TimeSpan.FromSeconds(10.5), end);
    }

    [Fact]
    public void Times_in_a_gap_clamp_to_the_end_of_the_span()
    {
        var (start, _) = Map.ToSource(TimeSpan.FromSeconds(2.05), TimeSpan.FromSeconds(2.06));

        Assert.Equal(TimeSpan.FromSeconds(4), start);
    }
}
