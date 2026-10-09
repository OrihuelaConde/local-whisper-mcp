using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Decodes audio files to 16 kHz mono samples, without ffmpeg for the common formats.</summary>
/// <remarks>
/// WAV, Ogg Opus, and MP3 decode with managed code. Any other format, or a file that the managed
/// decoders reject, goes to ffmpeg if it's on the <c>PATH</c>.
/// </remarks>
internal static class AudioDecoder
{
    /// <summary>The sample rate that Whisper expects, in hertz.</summary>
    public const int SampleRate = 16000;

    /// <summary>Decodes an audio file.</summary>
    /// <param name="path">The path to the audio file.</param>
    /// <param name="logger">The logger for fallbacks to ffmpeg.</param>
    /// <param name="cancellationToken">The token to cancel the decoding.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    /// <exception cref="AudioDecodingException">No decoder can read the file.</exception>
    public static async Task<float[]> DecodeAsync(string path, ILogger logger, CancellationToken cancellationToken)
    {
        var format = DetectFormat(path);
        if (format == AudioFormat.Other)
        {
            return await Ffmpeg.DecodeAsync(path, SampleRate, cancellationToken);
        }

        try
        {
            return format switch
            {
                AudioFormat.Wave => WaveDecoder.Decode(path, SampleRate),
                AudioFormat.Ogg => OggOpusDecoder.Decode(path, SampleRate),
                _ => Mp3Decoder.Decode(path, SampleRate),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!Ffmpeg.IsAvailable())
            {
                throw new AudioDecodingException($"Couldn't decode {Path.GetFileName(path)} as {format}: {exception.Message} Installing ffmpeg would add another decoder to try.", exception);
            }

            logger.LogInformation("The {Format} decoder failed on {File} ({Message}); trying ffmpeg.", format, Path.GetFileName(path), exception.Message);
            return await Ffmpeg.DecodeAsync(path, SampleRate, cancellationToken);
        }
    }

    /// <summary>Detects the container format from the first bytes of the file, or from its extension.</summary>
    /// <param name="path">The path to the audio file.</param>
    /// <returns>The format.</returns>
    public static AudioFormat DetectFormat(string path)
    {
        Span<byte> header = stackalloc byte[12];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        header = header[..read];
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            return AudioFormat.Wave;
        }

        if (header.StartsWith("OggS"u8))
        {
            return AudioFormat.Ogg;
        }

        // An MPEG audio frame starts with 11 set sync bits; layer bits 01 mean Layer III. ADTS AAC
        // shares the sync bits but uses layer bits 00.
        if (header.StartsWith("ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE6) == 0xE2))
        {
            return AudioFormat.Mp3;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => AudioFormat.Wave,
            ".ogg" or ".oga" or ".opus" => AudioFormat.Ogg,
            ".mp3" => AudioFormat.Mp3,
            _ => AudioFormat.Other,
        };
    }
}

/// <summary>Identifies an audio container format.</summary>
internal enum AudioFormat
{
    /// <summary>RIFF WAVE.</summary>
    Wave,

    /// <summary>Ogg, which the server decodes when it carries Opus.</summary>
    Ogg,

    /// <summary>MPEG-1 or MPEG-2 Audio Layer III.</summary>
    Mp3,

    /// <summary>Any other format, decoded with ffmpeg.</summary>
    Other,
}

/// <summary>The exception that is thrown when no decoder can read an audio file.</summary>
/// <param name="message">The message that describes the error.</param>
/// <param name="innerException">The exception that caused this one, if any.</param>
internal sealed class AudioDecodingException(string message, Exception? innerException = null) : Exception(message, innerException);
