namespace LocalWhisperMcp.Tests;

public sealed class AudioQualityTests
{
    private const int Rate = AudioDecoder.SampleRate;

    [Fact]
    public void Speech_with_quiet_pauses_measures_as_clean()
    {
        // Like a voice note: speech-level tone with pauses at the level of a quiet room.
        float[] samples = [.. Tone(0.2f, 3 * Rate), .. Tone(0.0005f, Rate), .. Tone(0.2f, 3 * Rate), .. Tone(0.0005f, Rate)];

        Assert.True(AudioQuality.MeasureSpreadDb(samples) >= AudioQuality.CleanSpreadDb);
    }

    [Fact]
    public void Speech_over_steady_background_noise_measures_as_noisy()
    {
        // Like a lecture recorded from the audience: the pauses are only about 15 dB below the speech.
        float[] samples = [.. Tone(0.06f, 3 * Rate), .. Noise(0.01f, Rate), .. Tone(0.06f, 3 * Rate), .. Noise(0.01f, Rate)];

        Assert.True(AudioQuality.MeasureSpreadDb(samples) < AudioQuality.CleanSpreadDb);
    }

    [Fact]
    public void Audio_shorter_than_a_frame_measures_zero()
    {
        Assert.Equal(0, AudioQuality.MeasureSpreadDb(new float[100]));
    }

    private static float[] Tone(float amplitude, int length) =>
        [.. Enumerable.Range(0, length).Select(i => amplitude * MathF.Sin(2 * MathF.PI * 220 * i / Rate))];

    private static float[] Noise(float amplitude, int length)
    {
        var random = new Random(1);
        return [.. Enumerable.Range(0, length).Select(_ => amplitude * ((float)random.NextDouble() * 2 - 1))];
    }
}
