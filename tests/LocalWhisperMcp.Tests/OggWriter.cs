using System.Buffers.Binary;

namespace LocalWhisperMcp.Tests;

/// <summary>Writes Ogg pages for tests, one packet sequence per page.</summary>
internal static class OggWriter
{
    /// <summary>Writes one Ogg page that holds the given packets.</summary>
    /// <param name="output">The stream that receives the page.</param>
    /// <param name="serial">The logical stream serial number.</param>
    /// <param name="granule">The granule position of the page.</param>
    /// <param name="packets">The packets, each completed on this page.</param>
    /// <remarks>The CRC field stays zero; the reader under test doesn't check it.</remarks>
    public static void WritePage(Stream output, uint serial, long granule, params byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var packet in packets)
        {
            // A packet takes as many 255 lacing values as fit, then one value below 255.
            for (var remaining = packet.Length; ; remaining -= 255)
            {
                lacing.Add((byte)Math.Min(remaining, 255));
                if (remaining < 255)
                {
                    break;
                }
            }
        }

        var header = new byte[27];
        "OggS"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        header[26] = (byte)lacing.Count;
        output.Write(header);
        output.Write(lacing.ToArray());
        foreach (var packet in packets)
        {
            output.Write(packet);
        }
    }

    /// <summary>Builds an <c>OpusHead</c> identification header.</summary>
    /// <param name="channels">The channel count.</param>
    /// <param name="preSkip">The samples to skip at 48 kHz.</param>
    /// <returns>The header packet.</returns>
    public static byte[] OpusHead(int channels, int preSkip)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        return head;
    }

    /// <summary>Builds an empty <c>OpusTags</c> comment header.</summary>
    /// <returns>The header packet.</returns>
    public static byte[] OpusTags()
    {
        var tags = new byte[16];
        "OpusTags"u8.CopyTo(tags);
        return tags;
    }
}
