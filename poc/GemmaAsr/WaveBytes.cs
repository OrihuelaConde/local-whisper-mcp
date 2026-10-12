using System.Buffers.Binary;

namespace GemmaAsr;

/// <summary>Encodes samples as a WAV file in memory, the form in which LiteRT-LM takes audio.</summary>
internal static class WaveBytes
{
    /// <summary>Encodes mono samples as 16-bit PCM.</summary>
    /// <param name="samples">The samples, in the range from -1 to 1.</param>
    /// <param name="sampleRate">The sample rate, in hertz.</param>
    /// <returns>The bytes of the WAV file.</returns>
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate)
    {
        const int HeaderSize = 44;
        var dataSize = samples.Length * 2;
        var bytes = new byte[HeaderSize + dataSize];
        var span = bytes.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderSize - 8 + dataSize);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        var data = span[HeaderSize..];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], (short)value);
        }

        return bytes;
    }
}
