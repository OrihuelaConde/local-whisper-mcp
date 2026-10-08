using System.Diagnostics;
using Concentus;
using Concentus.Oggfile;
using NLayer;
using Whisper.net.Wave;

/// <summary>Loads audio files as 16 kHz mono samples without depending on ffmpeg for common formats.</summary>
internal static class AudioLoader
{
    /// <summary>The sample rate that Whisper expects, in hertz.</summary>
    public const int SampleRate = 16000;

    /// <summary>Loads an audio file as 16 kHz mono samples.</summary>
    /// <param name="path">The path to the audio file.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    /// <exception cref="NotSupportedException">The format isn't supported and ffmpeg isn't on the <c>PATH</c>.</exception>
    public static async Task<float[]> LoadAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        Metrics.Log($"decoder: {extension switch { ".wav" => "WaveParser", ".ogg" or ".opus" => "Concentus", ".mp3" => "NLayer", _ => "ffmpeg" }}");
        return extension switch
        {
            ".wav" => await LoadWaveAsync(path),
            ".ogg" or ".opus" => LoadOpus(path),
            ".mp3" => LoadMp3(path),
            _ => await LoadWithFfmpegAsync(path),
        };
    }

    private static async Task<float[]> LoadWaveAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var parser = new WaveParser(stream);
        await parser.InitializeAsync();
        var samples = await parser.GetAvgSamplesAsync();
        return Resampler.Resample(samples, (int)parser.SampleRate, SampleRate);
    }

    private static float[] LoadOpus(string path)
    {
        // The managed decoder keeps the behavior identical on every platform.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;

        // Opus decodes natively at 16 kHz and downmixes to mono, so no resampling is needed.
        using var stream = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
        var reader = new OpusOggReadStream(decoder, stream);
        var samples = new List<float>();
        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is null)
            {
                continue;
            }

            foreach (var sample in packet)
            {
                samples.Add(sample / 32768f);
            }
        }

        return [.. samples];
    }

    private static float[] LoadMp3(string path)
    {
        using var file = new MpegFile(path);
        var channels = file.Channels;
        var buffer = new float[file.SampleRate * channels];
        var mono = new List<float>();
        int read;
        while ((read = file.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + channels <= read; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i + c];
                }

                mono.Add(sum / channels);
            }
        }

        return Resampler.Resample([.. mono], file.SampleRate, SampleRate);
    }

    private static async Task<float[]> LoadWithFfmpegAsync(string path)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-i", path, "-f", "s16le", "-ac", "1", "-ar", "16000", "-" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new NotSupportedException("ffmpeg didn't start.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new NotSupportedException($"Decoding '{Path.GetExtension(path)}' files requires ffmpeg on the PATH.");
        }

        using (process)
        {
            using var pcm = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.BaseStream.CopyToAsync(pcm);
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"ffmpeg failed: {await errorTask}");
            }

            var bytes = pcm.GetBuffer().AsSpan(0, (int)pcm.Length);
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(bytes.Slice(i * 2, 2)) / 32768f;
            }

            return samples;
        }
    }
}

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
