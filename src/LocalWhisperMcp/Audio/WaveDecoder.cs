using System.Buffers.Binary;

namespace LocalWhisperMcp;

/// <summary>Decodes RIFF WAVE files with integer PCM or floating-point samples.</summary>
internal static class WaveDecoder
{
    private const int FormatPcm = 1;
    private const int FormatFloat = 3;
    private const int FormatExtensible = 0xFFFE;

    /// <summary>Decodes a WAVE file to mono samples.</summary>
    /// <param name="path">The path to the file.</param>
    /// <param name="sampleRate">The output sample rate, in hertz.</param>
    /// <returns>The samples, averaged over the channels and normalized to the range from -1 to 1.</returns>
    /// <exception cref="NotSupportedException">The file uses a compressed or unusual sample format.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static float[] Decode(string path, int sampleRate)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[12];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Missing RIFF WAVE header.");
        }

        (int Format, int Channels, int Rate, int BlockAlign, int Bits)? format = null;
        var chunk = new byte[8];
        while (stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                var fmt = new byte[Math.Min(size, 64)];
                stream.ReadExactly(fmt);
                stream.Seek(size - fmt.Length + (size & 1), SeekOrigin.Current);
                format = ParseFormat(fmt);
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (format is not { } f)
                {
                    throw new InvalidDataException("The data chunk comes before the fmt chunk.");
                }

                // Recorders that stream to disk may leave the size at 0 or at its maximum.
                var available = stream.Length - stream.Position;
                var length = size is 0 or uint.MaxValue || size > available ? available : size;
                var samples = ReadSamples(stream, length / f.BlockAlign, f.Format, f.Channels, f.BlockAlign, f.Bits);
                return Resampler.Resample(samples, f.Rate, sampleRate);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("The file has no data chunk.");
    }

    private static (int Format, int Channels, int Rate, int BlockAlign, int Bits) ParseFormat(ReadOnlySpan<byte> fmt)
    {
        if (fmt.Length < 16)
        {
            throw new InvalidDataException("The fmt chunk is too short.");
        }

        int formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        // WAVE_FORMAT_EXTENSIBLE keeps the real format in the first two bytes of the subformat GUID.
        if (formatTag == FormatExtensible && fmt.Length >= 26)
        {
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        var supported = (formatTag, bits) is (FormatPcm, 8 or 16 or 24 or 32) or (FormatFloat, 32 or 64);
        if (!supported || channels == 0 || rate <= 0 || blockAlign < channels * bits / 8)
        {
            throw new NotSupportedException($"WAVE format {formatTag} with {bits}-bit samples isn't supported.");
        }

        return (formatTag, channels, rate, blockAlign, bits);
    }

    private static float[] ReadSamples(Stream stream, long frames, int format, int channels, int blockAlign, int bits)
    {
        var output = new float[frames];
        var bytesPerSample = bits / 8;
        var buffer = new byte[blockAlign * 4096];
        long frame = 0;
        while (frame < frames)
        {
            var count = (int)Math.Min(frames - frame, 4096);
            var read = stream.ReadAtLeast(buffer.AsSpan(0, count * blockAlign), count * blockAlign, throwOnEndOfStream: false);
            count = read / blockAlign;
            if (count == 0)
            {
                break;
            }

            for (var i = 0; i < count; i++)
            {
                var sum = 0.0;
                for (var c = 0; c < channels; c++)
                {
                    sum += ReadSample(buffer.AsSpan((i * blockAlign) + (c * bytesPerSample), bytesPerSample), format);
                }

                output[frame++] = (float)(sum / channels);
            }
        }

        return frame == frames ? output : output[..(int)frame];
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, int format) => (format, bytes.Length) switch
    {
        (FormatFloat, 4) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (FormatFloat, _) => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        (_, 1) => (bytes[0] - 128) / 128.0,
        (_, 2) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
        (_, 3) => ((bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8)) / 2147483648.0,
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
    };
}
