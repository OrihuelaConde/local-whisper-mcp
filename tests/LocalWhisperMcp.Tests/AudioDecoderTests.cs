using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWhisperMcp.Tests;

public sealed class AudioDecoderTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("lwm-audio-").FullName;

    [Theory]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, ".bin", "Wave")]
    [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0 }, ".m4a", "Ogg")]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 4, 0 }, ".tmp", "Mp3")]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, "", "Mp3")]
    [InlineData(new byte[] { 0xFF, 0xF1, 0x50, 0x80 }, ".mp3", "Mp3")]
    [InlineData(new byte[] { 0xFF, 0xF1, 0x50, 0x80 }, ".aac", "Other")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70 }, ".m4a", "Other")]
    [InlineData(new byte[] { 0, 1, 2 }, ".opus", "Ogg")]
    public void The_format_comes_from_the_content_before_the_extension(byte[] header, string extension, string expected)
    {
        var path = Path.Combine(directory, $"audio{extension}");
        File.WriteAllBytes(path, header);

        Assert.Equal(expected, AudioDecoder.DetectFormat(path).ToString());
    }

    [Fact]
    public async Task A_stereo_wave_file_is_mixed_down_and_resampled_to_16_kHz()
    {
        const int Rate = 8000;
        var path = Path.Combine(directory, "stereo.wav");
        WriteWave(path, Rate, channels: 2, [.. Enumerable.Range(0, Rate).SelectMany(_ => new short[] { 8000, 24000 })]);

        var samples = await AudioDecoder.DecodeAsync(path, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(AudioDecoder.SampleRate, samples.Length);
        // The channels average to 16,000 of 32,768.
        Assert.InRange(samples[AudioDecoder.SampleRate / 2], 0.48f, 0.5f);
    }

    [Theory]
    [InlineData(1, 8, new byte[] { 0xC0 })]
    [InlineData(1, 24, new byte[] { 0x00, 0x00, 0x40 })]
    [InlineData(1, 32, new byte[] { 0x00, 0x00, 0x00, 0x40 })]
    [InlineData(3, 32, new byte[] { 0x00, 0x00, 0x00, 0x3F })]
    [InlineData(0xFFFE, 24, new byte[] { 0x00, 0x00, 0x40 })]
    public void Wave_files_with_other_sample_formats_decode_to_the_same_level(int formatTag, int bits, byte[] sample)
    {
        var path = Path.Combine(directory, "format.wav");
        WriteRawWave(path, formatTag, bits, sample, AudioDecoder.SampleRate);

        var samples = WaveDecoder.Decode(path, AudioDecoder.SampleRate);

        Assert.Equal(AudioDecoder.SampleRate, samples.Length);
        Assert.All(samples, value => Assert.Equal(0.5f, value, 0.001f));
    }

    [Fact]
    public void Compressed_wave_files_are_not_supported()
    {
        // Format 2 is Microsoft ADPCM, which ffmpeg decodes when it's installed.
        var path = Path.Combine(directory, "adpcm.wav");
        WriteRawWave(path, 2, 4, [0], 10);

        Assert.Throws<NotSupportedException>(() => WaveDecoder.Decode(path, AudioDecoder.SampleRate));
    }

    [Fact]
    public void Resampling_keeps_the_duration_and_a_constant_level()
    {
        var input = Enumerable.Repeat(0.5f, 44100).ToArray();

        var output = Resampler.Resample(input, 44100, 16000);

        Assert.Equal(16000, output.Length);
        Assert.InRange(output[8000], 0.49f, 0.51f);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    // Writes a mono file that repeats one sample, with an extra chunk before fmt and an
    // extensible fmt chunk when the format tag asks for it.
    private static void WriteRawWave(string path, int formatTag, int bits, byte[] sample, int count)
    {
        var blockAlign = Math.Max(1, bits / 8);
        var extensible = formatTag == 0xFFFE;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("LIST"u8);
        writer.Write(3);
        writer.Write(new byte[4]);
        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write((short)formatTag);
        writer.Write((short)1);
        writer.Write(AudioDecoder.SampleRate);
        writer.Write(AudioDecoder.SampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bits);
        if (extensible)
        {
            writer.Write((short)22);
            writer.Write((short)bits);
            writer.Write(4);
            writer.Write((short)1);
            writer.Write(new byte[14]);
        }

        writer.Write("data"u8);
        writer.Write(sample.Length * count);
        for (var i = 0; i < count; i++)
        {
            writer.Write(sample);
        }
    }

    private static void WriteWave(string path, int sampleRate, int channels, short[] interleaved)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (interleaved.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(interleaved.Length * 2);
        foreach (var sample in interleaved)
        {
            writer.Write(sample);
        }
    }
}
