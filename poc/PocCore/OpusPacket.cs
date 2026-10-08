namespace PocCore;

/// <summary>Parses the frame layout of Opus packets, as defined in RFC 6716, section 3.</summary>
internal static class OpusPacket
{
    /// <summary>Gets the number of samples in each frame of a packet.</summary>
    /// <param name="toc">The table-of-contents byte, the first byte of the packet.</param>
    /// <param name="sampleRate">The output sample rate, in hertz.</param>
    /// <returns>The samples per channel in one frame.</returns>
    public static int GetFrameSamples(byte toc, int sampleRate)
    {
        var config = toc >> 3;

        // Frame durations in tenths of a millisecond: SILK 10-60 ms, hybrid 10-20 ms, CELT 2.5-20 ms.
        int tenths = config switch
        {
            < 12 => (config % 4) switch { 0 => 100, 1 => 200, 2 => 400, _ => 600 },
            < 16 => config % 2 == 0 ? 100 : 200,
            _ => (config % 4) switch { 0 => 25, 1 => 50, 2 => 100, _ => 200 },
        };
        return sampleRate * tenths / 10000;
    }

    /// <summary>Splits a packet into its compressed frames.</summary>
    /// <param name="packet">The Opus packet.</param>
    /// <returns>The offset and length of each frame inside <paramref name="packet"/>. A length of zero marks a frame that carries no data.</returns>
    /// <exception cref="InvalidDataException">The packet is malformed.</exception>
    public static List<(int Offset, int Length)> SplitFrames(byte[] packet)
    {
        if (packet.Length == 0)
        {
            throw new InvalidDataException("Empty Opus packet.");
        }

        var frames = new List<(int, int)>();
        var position = 1;
        switch (packet[0] & 3)
        {
            case 0:
                frames.Add((1, packet.Length - 1));
                break;
            case 1:
                var half = (packet.Length - 1) / 2;
                frames.Add((1, half));
                frames.Add((1 + half, half));
                break;
            case 2:
                var firstLength = ReadLength(packet, ref position);
                frames.Add((position, firstLength));
                frames.Add((position + firstLength, packet.Length - position - firstLength));
                break;
            default:
                var header = ReadByte(packet, ref position);
                var isVbr = (header & 0x80) != 0;
                var hasPadding = (header & 0x40) != 0;
                var count = header & 0x3F;
                var padding = 0;
                while (hasPadding)
                {
                    var value = ReadByte(packet, ref position);
                    padding += value == 255 ? 254 : value;
                    hasPadding = value == 255;
                }

                var lengths = new int[count];
                var available = packet.Length - position - padding;
                if (isVbr)
                {
                    var sum = 0;
                    for (var i = 0; i < count - 1; i++)
                    {
                        lengths[i] = ReadLength(packet, ref position);
                        sum += lengths[i];
                    }

                    available = packet.Length - position - padding;
                    lengths[count - 1] = available - sum;
                }
                else if (count > 0)
                {
                    Array.Fill(lengths, available / count);
                }

                foreach (var length in lengths)
                {
                    if (length < 0 || position + length > packet.Length - padding)
                    {
                        throw new InvalidDataException("Malformed Opus packet.");
                    }

                    frames.Add((position, length));
                    position += length;
                }

                break;
        }

        return frames;
    }

    private static byte ReadByte(byte[] packet, ref int position) =>
        position < packet.Length ? packet[position++] : throw new InvalidDataException("Truncated Opus packet.");

    private static int ReadLength(byte[] packet, ref int position)
    {
        int first = ReadByte(packet, ref position);
        return first < 252 ? first : first + (4 * ReadByte(packet, ref position));
    }
}
