using Concentus;

namespace LocalWhisperMcp;

/// <summary>Decodes Ogg Opus files, such as WhatsApp and Telegram voice notes, with Concentus.</summary>
internal static class OggOpusDecoder
{
    /// <summary>Decodes an Ogg Opus file to mono samples.</summary>
    /// <param name="path">The path to the file.</param>
    /// <param name="sampleRate">The output sample rate: 8, 12, 16, 24, or 48 kHz.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    /// <exception cref="NotSupportedException">The Ogg stream doesn't carry mono or stereo Opus.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static float[] Decode(string path, int sampleRate)
    {
        // The managed decoder keeps the behavior identical on every platform.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;

        // Opus decodes natively at the requested rate and downmixes to mono, so no resampling is
        // needed. Ogg granule positions and the pre-skip always count 48 kHz samples.
        var granuleRatio = 48000 / sampleRate;
        using var stream = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1);
        var frame = new float[sampleRate * 120 / 1000];
        var samples = new List<float>();
        var preSkip = 0;
        var packetIndex = 0;
        var single = new byte[1276];
        long lastGranule = 0;
        foreach (var (packet, granule) in OggReader.ReadPackets(stream))
        {
            lastGranule = granule;
            switch (packetIndex++)
            {
                case 0:
                    if (!packet.AsSpan().StartsWith("OpusHead"u8))
                    {
                        throw new NotSupportedException("The Ogg stream doesn't contain Opus audio.");
                    }

                    if (packet.Length < 19 || packet[9] is 0 or > 2)
                    {
                        throw new NotSupportedException("Only mono and stereo Opus streams are supported.");
                    }

                    preSkip = BitConverter.ToUInt16(packet, 10);
                    continue;
                case 1:
                    // OpusTags carries only metadata.
                    continue;
            }

            if (packet.Length == 0)
            {
                continue;
            }

            // Concentus rejects packets that contain zero-length frames, which WhatsApp sends for
            // silence (DTX). Each frame is decoded as its own single-frame packet instead, and an
            // empty frame becomes packet loss concealment, as libopus does.
            var frameSamples = OpusPacket.GetFrameSamples(packet[0], sampleRate);
            foreach (var (offset, frameLength) in OpusPacket.SplitFrames(packet))
            {
                int decoded;
                if (frameLength == 0)
                {
                    decoded = decoder.Decode(ReadOnlySpan<byte>.Empty, frame, frameSamples);
                }
                else
                {
                    single[0] = (byte)(packet[0] & 0xFC);
                    packet.AsSpan(offset, frameLength).CopyTo(single.AsSpan(1));
                    decoded = decoder.Decode(single.AsSpan(0, frameLength + 1), frame, frame.Length);
                }

                samples.AddRange(frame.AsSpan(0, decoded));
            }
        }

        if (packetIndex == 0)
        {
            throw new InvalidDataException("The Ogg file contains no packets.");
        }

        // Drop the encoder delay at the start and the padding after the last granule position.
        var first = Math.Min(samples.Count, preSkip / granuleRatio);
        var length = (int)Math.Clamp((lastGranule - preSkip) / granuleRatio, 0, samples.Count - first);
        return [.. samples.GetRange(first, length)];
    }
}
