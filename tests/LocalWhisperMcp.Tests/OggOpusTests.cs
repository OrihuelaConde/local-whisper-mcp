using Concentus;
using Concentus.Enums;

namespace LocalWhisperMcp.Tests;

public sealed class OggOpusTests : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameSamples = SampleRate / 50;
    private readonly string directory = Directory.CreateTempSubdirectory("lwm-ogg-").FullName;

    [Fact]
    public void The_reader_joins_packets_that_span_lacing_segments_and_skips_other_streams()
    {
        var large = Enumerable.Range(0, 600).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream();
        OggWriter.WritePage(stream, serial: 7, granule: 0, [1, 2, 3]);
        OggWriter.WritePage(stream, serial: 9, granule: 0, [9, 9]);
        OggWriter.WritePage(stream, serial: 7, granule: 960, large, [4]);
        stream.Position = 0;

        var packets = OggReader.ReadPackets(stream).ToList();

        Assert.Equal(3, packets.Count);
        Assert.Equal([1, 2, 3], packets[0].Packet);
        Assert.Equal(large, packets[1].Packet);
        Assert.Equal(960, packets[1].Granule);
        Assert.Equal([4], packets[2].Packet);
    }

    [Fact]
    public void The_reader_stops_at_the_end_of_a_stream_without_the_end_flag()
    {
        // WhatsApp ends its streams this way; Concentus.OggFile loops forever on them.
        using var stream = new MemoryStream();
        OggWriter.WritePage(stream, serial: 1, granule: 0, [1]);
        stream.Write([0x4F, 0x67]);
        stream.Position = 0;

        Assert.Single(OggReader.ReadPackets(stream));
    }

    [Theory]
    [InlineData(0x00, 160)] // SILK narrowband, 10 ms.
    [InlineData(0x18, 960)] // SILK narrowband, 60 ms.
    [InlineData(0x70, 160)] // Hybrid fullband, 10 ms.
    [InlineData(0xE0, 40)] // CELT fullband, 2.5 ms.
    public void Frame_durations_follow_the_toc_configuration(byte toc, int samples) =>
        Assert.Equal(samples, OpusPacket.GetFrameSamples(toc, SampleRate));

    [Fact]
    public void Packets_split_into_frames_for_every_frame_count_code()
    {
        Assert.Equal([(1, 3)], OpusPacket.SplitFrames([0x08, 1, 2, 3]));
        Assert.Equal([(1, 2), (3, 2)], OpusPacket.SplitFrames([0x09, 1, 2, 3, 4]));
        Assert.Equal([(2, 1), (3, 3)], OpusPacket.SplitFrames([0x0A, 1, 9, 8, 7, 6]));

        // Code 3, variable bitrate, three frames: lengths 2 and 0 are explicit; the last takes the rest.
        Assert.Equal([(4, 2), (6, 0), (6, 1)], OpusPacket.SplitFrames([0x0B, 0x83, 2, 0, 5, 6, 7]));

        // Code 3, constant bitrate, two frames with one byte of padding.
        Assert.Equal([(3, 2), (5, 2)], OpusPacket.SplitFrames([0x0B, 0x42, 1, 5, 6, 7, 8, 0]));
    }

    [Fact]
    public void Malformed_packets_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => OpusPacket.SplitFrames([]));
        Assert.Throws<InvalidDataException>(() => OpusPacket.SplitFrames([0x0B]));
        Assert.Throws<InvalidDataException>(() => OpusPacket.SplitFrames([0x0B, 0x82, 9, 1]));
    }

    [Fact]
    public void An_ogg_opus_file_decodes_to_its_granule_length_without_the_pre_skip()
    {
        const int Frames = 50;
        const int PreSkip = 312;
        var source = Tone(Frames * FrameSamples);
        var packets = Encode(source);
        var path = WriteOggOpus(packets, PreSkip, Frames * FrameSamples * 3);

        var decoded = OggOpusDecoder.Decode(path, SampleRate);

        Assert.Equal((Frames * FrameSamples * 3 - PreSkip) / 3, decoded.Length);
        Assert.InRange(Rms(decoded.AsSpan(SampleRate / 4)), 0.2, 0.5);
    }

    [Fact]
    public void Empty_frames_from_discontinuous_transmission_decode_as_concealment()
    {
        // WhatsApp sends silence as 120 ms packets whose 20 ms SILK frames can be empty. Concentus
        // rejects such packets as a whole, so the decoder splits them into frames.
        var frames = Encode(Tone(2 * FrameSamples));
        var first = frames[0].AsSpan(1).ToArray();
        byte[] mixed = [(byte)(frames[0][0] | 3), 0x82, (byte)first.Length, .. first];
        var path = WriteOggOpus([mixed], preSkip: 0, granule: 2 * FrameSamples * 3);

        var decoded = OggOpusDecoder.Decode(path, SampleRate);

        Assert.Equal(2 * FrameSamples, decoded.Length);
    }

    [Fact]
    public void A_stream_that_isnt_opus_is_not_supported()
    {
        var path = Path.Combine(directory, "vorbis.ogg");
        using (var file = File.Create(path))
        {
            OggWriter.WritePage(file, 1, 0, [0x01, .. "vorbis"u8]);
        }

        Assert.Throws<NotSupportedException>(() => OggOpusDecoder.Decode(path, SampleRate));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static float[] Tone(int length) =>
        [.. Enumerable.Range(0, length).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / SampleRate))];

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static List<byte[]> Encode(float[] samples)
    {
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var output = new byte[1275];
        var packets = new List<byte[]>();
        for (var offset = 0; offset + FrameSamples <= samples.Length; offset += FrameSamples)
        {
            var length = encoder.Encode(samples.AsSpan(offset, FrameSamples), FrameSamples, output, output.Length);
            packets.Add(output[..length]);
        }

        return packets;
    }

    private string WriteOggOpus(IReadOnlyList<byte[]> packets, int preSkip, long granule)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.ogg");
        using var file = File.Create(path);
        OggWriter.WritePage(file, 1, 0, OggWriter.OpusHead(1, preSkip));
        OggWriter.WritePage(file, 1, 0, OggWriter.OpusTags());
        OggWriter.WritePage(file, 1, granule, [.. packets]);
        return path;
    }
}
