using System.Text;

namespace LocalWhisperMcp.Tests;

/// <summary>Compares the managed decoders with ffmpeg on real recordings.</summary>
/// <remarks>
/// Voice notes can be private, so they stay out of the repository. Set
/// <c>LOCAL_WHISPER_TEST_AUDIO</c> to a folder of WAV, Ogg Opus, and MP3 files, and install
/// ffmpeg, to run this test; otherwise it's skipped.
/// </remarks>
public sealed class DecoderParityTests
{
    [Fact]
    public async Task Managed_decoders_match_ffmpeg_in_length_and_alignment()
    {
        var folder = Environment.GetEnvironmentVariable("LOCAL_WHISPER_TEST_AUDIO");
        if (folder is null || !Directory.Exists(folder) || !Ffmpeg.IsAvailable())
        {
            Assert.Skip("Set LOCAL_WHISPER_TEST_AUDIO to a folder of audio files and install ffmpeg to run this test.");
        }

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".ogg" or ".opus" or ".mp3")
            .ToList();
        Assert.NotEmpty(files);

        var report = new StringBuilder();
        var failures = 0;
        foreach (var file in files)
        {
            var format = AudioDecoder.DetectFormat(file);
            var managed = format switch
            {
                AudioFormat.Wave => WaveDecoder.Decode(file, AudioDecoder.SampleRate),
                AudioFormat.Ogg => OggOpusDecoder.Decode(file, AudioDecoder.SampleRate),
                _ => Mp3Decoder.Decode(file, AudioDecoder.SampleRate),
            };
            var reference = await Ffmpeg.DecodeAsync(file, AudioDecoder.SampleRate, TestContext.Current.CancellationToken);

            // Resamplers differ by a few samples at the end; a delay that isn't trimmed shows up as
            // a low correlation.
            var lengthDifference = Math.Abs(managed.Length - reference.Length);
            var correlation = Correlation(managed, reference);
            var passed = lengthDifference <= 32 + (reference.Length / 1000) && correlation >= 0.99;
            failures += passed ? 0 : 1;
            report.AppendLine($"{(passed ? "ok  " : "FAIL")} {Path.GetRelativePath(folder, file)}: {managed.Length} vs {reference.Length} samples, correlation {correlation:F4}");
        }

        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.True(failures == 0, report.ToString());
    }

    private static double Correlation(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? (normA == normB ? 1 : 0) : dot / Math.Sqrt(normA * normB);
    }
}
