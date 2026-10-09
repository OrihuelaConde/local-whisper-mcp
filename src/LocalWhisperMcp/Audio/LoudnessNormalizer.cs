namespace LocalWhisperMcp;

/// <summary>Evens out the loudness of audio over time, like ffmpeg's dynaudnorm filter.</summary>
/// <remarks>
/// Silero VAD misses quiet speech: in a lecture recorded from the audience (mean level -30 dBFS,
/// peaks at 0 dBFS), it found speech in 6% of the audio. Raising each half-second toward a common
/// peak, with the gain smoothed across neighboring frames and capped, brought that to 39% without
/// making it fire on noise in the synthetic samples.
/// </remarks>
internal static class LoudnessNormalizer
{
    // dynaudnorm's defaults: 500 ms frames, a 31-frame Gaussian window, a 0.95 target peak, and a
    // maximum gain of 10 (20 dB).
    private const int FrameSamples = AudioDecoder.SampleRate / 2;
    private const int HalfWindow = 15;
    private const float TargetPeak = 0.95f;
    private const float MaxGain = 10f;

    /// <summary>Returns a copy of the samples with the loudness evened out.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <returns>The normalized samples, in the range from -1 to 1.</returns>
    public static float[] Normalize(float[] samples)
    {
        var frames = (samples.Length + FrameSamples - 1) / FrameSamples;
        if (frames == 0)
        {
            return [];
        }

        // The gain that brings each frame's peak to the target, capped so silence isn't blown up.
        var gains = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var peak = 0f;
            var end = Math.Min(samples.Length, (frame + 1) * FrameSamples);
            for (var i = frame * FrameSamples; i < end; i++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i]));
            }

            gains[frame] = peak <= TargetPeak / MaxGain ? MaxGain : TargetPeak / peak;
        }

        var smoothed = Smooth(MinimumFilter(gains));
        var output = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            // Interpolate between frame centers so the gain changes without steps.
            var position = ((i + 0.5) / FrameSamples) - 0.5;
            var left = Math.Clamp((int)Math.Floor(position), 0, frames - 1);
            var right = Math.Min(left + 1, frames - 1);
            var fraction = (float)Math.Clamp(position - left, 0, 1);
            var gain = smoothed[left] + ((smoothed[right] - smoothed[left]) * fraction);
            output[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }

        return output;
    }

    // Taking the smallest gain nearby keeps a loud frame from being clipped by its quiet neighbors.
    private static float[] MinimumFilter(float[] gains)
    {
        var result = new float[gains.Length];
        for (var i = 0; i < gains.Length; i++)
        {
            var minimum = float.MaxValue;
            for (var j = Math.Max(0, i - HalfWindow); j <= Math.Min(gains.Length - 1, i + HalfWindow); j++)
            {
                minimum = Math.Min(minimum, gains[j]);
            }

            result[i] = minimum;
        }

        return result;
    }

    private static float[] Smooth(float[] gains)
    {
        const double Sigma = HalfWindow / 3.0;
        var result = new float[gains.Length];
        for (var i = 0; i < gains.Length; i++)
        {
            double sum = 0, weights = 0;
            for (var j = Math.Max(0, i - HalfWindow); j <= Math.Min(gains.Length - 1, i + HalfWindow); j++)
            {
                var weight = Math.Exp(-((i - j) * (i - j)) / (2 * Sigma * Sigma));
                sum += gains[j] * weight;
                weights += weight;
            }

            result[i] = (float)(sum / weights);
        }

        return result;
    }
}
