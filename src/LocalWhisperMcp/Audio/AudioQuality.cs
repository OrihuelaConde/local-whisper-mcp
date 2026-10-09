namespace LocalWhisperMcp;

/// <summary>Measures how far speech stands above the background, to decide whether VAD can be trusted.</summary>
/// <remarks>
/// Silero VAD works on clean recordings, where it skips silence and keeps timestamps exact, but on a
/// lecture recorded from the audience it missed a third of the speech. The level spread separates
/// the two: voice notes measured 41 to 46 dB between their loud and quiet moments, while the lecture
/// and the synthetic samples with background noise measured 18 to 19 dB.
/// </remarks>
internal static class AudioQuality
{
    /// <summary>The level spread from which the background counts as quiet enough for VAD.</summary>
    public const double CleanSpreadDb = 30;

    private const int FrameSamples = AudioDecoder.SampleRate / 20;
    private const double SilenceDb = -120;

    /// <summary>Measures the spread between the loud and the quiet moments of a recording.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <returns>The difference, in decibels, between the 90th and the 10th percentile of the 50 ms frame levels.</returns>
    public static double MeasureSpreadDb(float[] samples)
    {
        var frames = samples.Length / FrameSamples;
        if (frames == 0)
        {
            return 0;
        }

        var levels = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var span = samples.AsSpan(frame * FrameSamples, FrameSamples);
            foreach (var sample in span)
            {
                sum += sample * sample;
            }

            var rms = Math.Sqrt(sum / FrameSamples);
            levels[frame] = rms > 0 ? Math.Max(SilenceDb, 20 * Math.Log10(rms)) : SilenceDb;
        }

        Array.Sort(levels);
        return levels[(int)(0.9 * (frames - 1))] - levels[(int)(0.1 * (frames - 1))];
    }
}
