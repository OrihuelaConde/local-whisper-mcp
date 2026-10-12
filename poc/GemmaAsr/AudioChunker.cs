using LocalWhisperMcp;

namespace GemmaAsr;

/// <summary>Splits audio into pieces short enough for the Gemma 4 audio encoder.</summary>
/// <remarks>
/// Gemma 4 accepts at most 30 seconds of audio per clip. Cutting at a fixed interval would split
/// words, so each cut lands on the quietest moment near the end of the piece.
/// </remarks>
internal static class AudioChunker
{
    private const int FrameSamples = AudioDecoder.SampleRate / 50;

    // A cut needs this much quiet audio around it, which is about the length of a short pause.
    private const int QuietFrames = 10;

    /// <summary>Splits the samples into consecutive pieces.</summary>
    /// <param name="samples">The 16 kHz mono samples.</param>
    /// <param name="maxSeconds">The longest piece, in seconds.</param>
    /// <param name="searchSeconds">How far back from the limit to look for a quiet moment to cut at.</param>
    /// <returns>The start and length of each piece, in samples.</returns>
    public static IReadOnlyList<(int Start, int Length)> Split(float[] samples, double maxSeconds, double searchSeconds)
    {
        var max = (int)(maxSeconds * AudioDecoder.SampleRate);
        var search = (int)(Math.Min(searchSeconds, maxSeconds / 2) * AudioDecoder.SampleRate);
        var pieces = new List<(int Start, int Length)>();
        var start = 0;
        while (samples.Length - start > max)
        {
            // A piece of a second or less makes the model invent text, so when the usual cut would
            // leave a short tail, the cut moves to the middle of what is left.
            var remaining = samples.Length - start;
            var cut = remaining < max + search
                ? FindQuietestPoint(samples, start + ((remaining - search) / 2), start + ((remaining + search) / 2))
                : FindQuietestPoint(samples, start + max - search, start + max);
            pieces.Add((start, cut - start));
            start = cut;
        }

        if (samples.Length > start)
        {
            pieces.Add((start, samples.Length - start));
        }

        return pieces;
    }

    // Returns the middle of the quietest run of frames between the two positions.
    private static int FindQuietestPoint(float[] samples, int from, int to)
    {
        var frames = (to - from) / FrameSamples;
        if (frames <= QuietFrames)
        {
            return to;
        }

        var energy = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            foreach (var sample in samples.AsSpan(from + (frame * FrameSamples), FrameSamples))
            {
                sum += sample * sample;
            }

            energy[frame] = sum;
        }

        var window = energy.Take(QuietFrames).Sum();
        var best = window;
        var bestFrame = 0;
        for (var frame = 1; frame + QuietFrames <= frames; frame++)
        {
            window += energy[frame + QuietFrames - 1] - energy[frame - 1];

            // Ties go to the later frame, which keeps the pieces long.
            if (window <= best)
            {
                best = window;
                bestFrame = frame;
            }
        }

        return from + ((bestFrame + (QuietFrames / 2)) * FrameSamples);
    }
}
