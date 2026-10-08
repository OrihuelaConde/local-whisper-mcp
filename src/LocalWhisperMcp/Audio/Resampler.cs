namespace LocalWhisperMcp;

/// <summary>Converts mono samples between sample rates with a windowed-sinc filter.</summary>
internal static class Resampler
{
    private const int HalfTaps = 16;

    /// <summary>Resamples mono samples to the target rate.</summary>
    /// <param name="input">The source samples.</param>
    /// <param name="sourceRate">The source sample rate, in hertz.</param>
    /// <param name="targetRate">The target sample rate, in hertz.</param>
    /// <returns>The resampled samples, or <paramref name="input"/> if the rates match.</returns>
    public static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate)
        {
            return input;
        }

        var ratio = (double)sourceRate / targetRate;

        // Downsampling lowers the cutoff to the target Nyquist frequency to avoid aliasing.
        var cutoff = Math.Min(1.0, 1.0 / ratio);
        var output = new float[(long)(input.Length / ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var center = i * ratio;
            var first = (int)Math.Floor(center) - HalfTaps + 1;
            double sum = 0;
            for (var j = first; j < first + (2 * HalfTaps); j++)
            {
                if (j < 0 || j >= input.Length)
                {
                    continue;
                }

                var x = center - j;
                sum += input[j] * cutoff * Sinc(cutoff * x) * Blackman(x / HalfTaps);
            }

            output[i] = (float)sum;
        }

        return output;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double x)
    {
        if (Math.Abs(x) >= 1)
        {
            return 0;
        }

        var t = (x + 1) / 2;
        return 0.42 - (0.5 * Math.Cos(2 * Math.PI * t)) + (0.08 * Math.Cos(4 * Math.PI * t));
    }
}
