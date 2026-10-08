namespace PocCore;

/// <summary>Reads the packets of the first logical stream in an Ogg file.</summary>
/// <remarks>
/// Concentus.OggFile loops forever on some WhatsApp voice notes, which end without the
/// end-of-stream flag, so the server demuxes Ogg itself and uses Concentus only to decode Opus.
/// </remarks>
internal static class OggReader
{
    /// <summary>Reads every complete packet of the first logical stream.</summary>
    /// <param name="stream">The Ogg data.</param>
    /// <returns>Each packet with the granule position of the page where it ends.</returns>
    /// <exception cref="InvalidDataException">The data isn't a valid Ogg stream.</exception>
    public static IEnumerable<(byte[] Packet, long Granule)> ReadPackets(Stream stream)
    {
        var header = new byte[27];
        var lacing = new byte[255];
        var packet = new MemoryStream();
        uint? serial = null;
        while (ReadFully(stream, header))
        {
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException("Missing Ogg page signature.");
            }

            var granule = BitConverter.ToInt64(header, 6);
            var pageSerial = BitConverter.ToUInt32(header, 14);
            var segments = header[26];
            if (!ReadFully(stream, lacing.AsSpan(0, segments)))
            {
                yield break;
            }

            serial ??= pageSerial;
            var bodyLength = 0;
            for (var i = 0; i < segments; i++)
            {
                bodyLength += lacing[i];
            }

            var body = new byte[bodyLength];
            if (!ReadFully(stream, body))
            {
                yield break;
            }

            if (pageSerial != serial)
            {
                continue;
            }

            var offset = 0;
            for (var i = 0; i < segments; i++)
            {
                packet.Write(body, offset, lacing[i]);
                offset += lacing[i];

                // A lacing value below 255 ends the packet; 255 continues it in the next segment.
                if (lacing[i] < 255)
                {
                    yield return (packet.ToArray(), granule);
                    packet.SetLength(0);
                }
            }
        }
    }

    private static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
