using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWhisperMcp.Tests;

/// <summary>Runs the Parakeet model itself, when <c>LOCAL_WHISPER_TEST_PARAKEET</c> names its GGUF file.</summary>
public sealed class ParakeetModelTests
{
    private static readonly string? ModelPath = Environment.GetEnvironmentVariable("LOCAL_WHISPER_TEST_PARAKEET");

    [Fact]
    public async Task Cancellation_stops_a_long_transcription_quickly()
    {
        Assert.SkipWhen(ModelPath is null, "Set LOCAL_WHISPER_TEST_PARAKEET to the path of parakeet-ultra-Q8_0.gguf to run this test.");
        using var model = new ParakeetModel(ModelPath!, "cpu", 8, NullLogger.Instance);

        // Ten minutes of a tone: long enough that the run is still going when the token fires.
        var samples = new float[AudioDecoder.SampleRate * 600];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.1f * MathF.Sin(2 * MathF.PI * 440 * i / AudioDecoder.SampleRate);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.TranscribeAsync(samples, new SpeechRequest("es"), null, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The run took {watch.Elapsed.TotalSeconds:F1} s to stop.");
    }

    [Fact]
    public async Task Silence_gives_no_segments()
    {
        Assert.SkipWhen(ModelPath is null, "Set LOCAL_WHISPER_TEST_PARAKEET to the path of parakeet-ultra-Q8_0.gguf to run this test.");
        using var model = new ParakeetModel(ModelPath!, "cpu", 8, NullLogger.Instance);

        var result = await model.TranscribeAsync(new float[AudioDecoder.SampleRate * 5], new SpeechRequest("auto"), null, CancellationToken.None);

        Assert.Empty(result.Segments);
        Assert.Null(result.Language);
    }
}
