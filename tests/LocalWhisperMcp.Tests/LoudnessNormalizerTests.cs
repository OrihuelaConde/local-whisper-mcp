namespace LocalWhisperMcp.Tests;

public sealed class LoudnessNormalizerTests
{
    private const int Rate = AudioDecoder.SampleRate;

    [Fact]
    public void Quiet_speech_is_raised_and_loud_speech_keeps_its_level()
    {
        // Thirty seconds of a quiet tone followed by ten seconds of a loud one. The gain looks
        // 15 frames (7.5 s) ahead and behind, so the start of the quiet part gets its own gain.
        var samples = Tone(0.02f, 30 * Rate).Concat(Tone(0.9f, 10 * Rate)).ToArray();

        var normalized = LoudnessNormalizer.Normalize(samples);

        Assert.Equal(samples.Length, normalized.Length);
        Assert.InRange(Peak(normalized, 2 * Rate, 3 * Rate), 0.15f, 0.25f);
        Assert.InRange(Peak(normalized, 37 * Rate, 38 * Rate), 0.85f, 0.96f);
    }

    [Fact]
    public void Silence_gains_at_most_20_dB()
    {
        var samples = Tone(0.001f, 5 * Rate);

        var normalized = LoudnessNormalizer.Normalize(samples);

        Assert.InRange(Peak(normalized, Rate, 2 * Rate), 0.009f, 0.011f);
    }

    [Fact]
    public void Empty_audio_stays_empty()
    {
        Assert.Empty(LoudnessNormalizer.Normalize([]));
    }

    private static float[] Tone(float amplitude, int length) =>
        [.. Enumerable.Range(0, length).Select(i => amplitude * MathF.Sin(2 * MathF.PI * 220 * i / Rate))];

    private static float Peak(float[] samples, int start, int end) => samples[start..end].Max(Math.Abs);
}
