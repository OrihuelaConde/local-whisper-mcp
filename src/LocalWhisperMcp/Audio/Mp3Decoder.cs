using System.Buffers.Binary;
using NLayer;

namespace LocalWhisperMcp;

/// <summary>Decodes MP3 files with NLayer and trims the encoder delay and padding, as ffmpeg does.</summary>
internal static class Mp3Decoder
{
    // An MP3 decoder outputs its samples 528 + 1 samples late.
    private const int DecoderDelay = 529;

    /// <summary>Decodes an MP3 file to mono samples.</summary>
    /// <param name="path">The path to the file.</param>
    /// <param name="sampleRate">The output sample rate, in hertz.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    public static float[] Decode(string path, int sampleRate)
    {
        GaplessInfo? gapless;
        using (var stream = File.OpenRead(path))
        {
            gapless = GaplessInfo.Read(stream);
        }

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

        var (start, length) = GetTrim(mono.Count, gapless);
        return Resampler.Resample([.. mono.GetRange(start, length)], file.SampleRate, sampleRate);
    }

    /// <summary>Computes the samples to keep after removing the encoder delay and padding.</summary>
    /// <param name="decodedSamples">The number of samples the decoder produced.</param>
    /// <param name="gapless">The delay and padding from the LAME tag, or <see langword="null"/> if the file has none.</param>
    /// <returns>The index of the first sample to keep and the number of samples to keep.</returns>
    public static (int Start, int Length) GetTrim(int decodedSamples, GaplessInfo? gapless)
    {
        if (gapless is not { } info)
        {
            return (0, decodedSamples);
        }

        // NLayer skips the Xing frame, so the decoded samples line up with the frame count.
        var start = Math.Min(decodedSamples, info.Delay + DecoderDelay);
        var total = info.Frames is { } frames ? (long)frames * info.SamplesPerFrame : decodedSamples;
        var length = Math.Clamp(total - info.Delay - info.Padding, 0, decodedSamples - start);
        return (start, (int)length);
    }
}

/// <summary>Holds the encoder delay and padding from the Xing and LAME tags of an MP3 file.</summary>
/// <param name="Delay">The samples the encoder added at the start.</param>
/// <param name="Padding">The samples the encoder added at the end.</param>
/// <param name="Frames">The number of audio frames, or <see langword="null"/> if the Xing tag doesn't say.</param>
/// <param name="SamplesPerFrame">The samples in each frame: 1,152 for MPEG-1 and 576 for MPEG-2 and 2.5.</param>
internal readonly record struct GaplessInfo(int Delay, int Padding, int? Frames, int SamplesPerFrame)
{
    /// <summary>Reads the gapless information from the first frame of an MP3 stream.</summary>
    /// <param name="stream">The MP3 data, positioned at the start.</param>
    /// <returns>The information, or <see langword="null"/> if the first frame has no LAME tag.</returns>
    /// <remarks>
    /// Like ffmpeg, it trusts the delay and padding only when the encoder string starts with
    /// <c>LAME</c>, <c>Lavf</c>, or <c>Lavc</c>.
    /// </remarks>
    public static GaplessInfo? Read(Stream stream)
    {
        var data = new byte[64 * 1024];
        var length = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        var bytes = data.AsSpan(0, length);

        var offset = 0;
        if (bytes.Length >= 10 && bytes.StartsWith("ID3"u8))
        {
            // The ID3v2 size is a 28-bit syncsafe integer that excludes the header and the footer.
            var size = (bytes[6] << 21) | (bytes[7] << 14) | (bytes[8] << 7) | bytes[9];
            offset = 10 + size + ((bytes[5] & 0x10) != 0 ? 10 : 0);
        }

        var frame = FindFrame(bytes, offset);
        if (frame < 0)
        {
            return null;
        }

        var isMpeg1 = ((bytes[frame + 1] >> 3) & 3) == 3;
        var hasCrc = (bytes[frame + 1] & 1) == 0;
        var isMono = (bytes[frame + 3] >> 6) == 3;
        var sideInfo = isMpeg1 ? (isMono ? 17 : 32) : (isMono ? 9 : 17);
        var tag = frame + 4 + (hasCrc ? 2 : 0) + sideInfo;
        if (tag + 8 > bytes.Length || !(bytes.Slice(tag, 4).SequenceEqual("Xing"u8) || bytes.Slice(tag, 4).SequenceEqual("Info"u8)))
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadInt32BigEndian(bytes[(tag + 4)..]);
        var position = tag + 8;
        int? frames = null;
        if ((flags & 1) != 0 && position + 4 <= bytes.Length)
        {
            frames = BinaryPrimitives.ReadInt32BigEndian(bytes[position..]);
        }

        position += ((flags & 1) != 0 ? 4 : 0) + ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
        if (position + 24 > bytes.Length)
        {
            return null;
        }

        var encoder = bytes.Slice(position, 4);
        if (!(encoder.SequenceEqual("LAME"u8) || encoder.SequenceEqual("Lavf"u8) || encoder.SequenceEqual("Lavc"u8)))
        {
            return null;
        }

        // Bytes 21 to 23 of the LAME tag hold two 12-bit values: the delay and the padding.
        var delay = (bytes[position + 21] << 4) | (bytes[position + 22] >> 4);
        var padding = ((bytes[position + 22] & 0x0F) << 8) | bytes[position + 23];
        return new GaplessInfo(delay, padding, frames is > 0 ? frames : null, isMpeg1 ? 1152 : 576);
    }

    private static int FindFrame(ReadOnlySpan<byte> bytes, int offset)
    {
        for (var i = offset; i >= 0 && i + 4 <= bytes.Length; i++)
        {
            // Sync bits, Layer III, a valid bitrate index, and a valid sample rate index.
            if (bytes[i] == 0xFF && (bytes[i + 1] & 0xE6) == 0xE2 && (bytes[i + 2] >> 4) is not (0 or 15) && ((bytes[i + 2] >> 2) & 3) != 3)
            {
                return i;
            }
        }

        return -1;
    }
}
