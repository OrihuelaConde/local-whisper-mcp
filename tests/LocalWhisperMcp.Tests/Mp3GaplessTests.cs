using System.Buffers.Binary;

namespace LocalWhisperMcp.Tests;

public sealed class Mp3GaplessTests
{
    [Fact]
    public void The_lame_tag_after_an_id3_tag_gives_the_delay_padding_and_frames()
    {
        var info = GaplessInfo.Read(new MemoryStream(FirstFrame(id3Size: 35, encoder: "Lavc62.28")));

        Assert.Equal(new GaplessInfo(Delay: 576, Padding: 932, Frames: 678, SamplesPerFrame: 576), info);
    }

    [Fact]
    public void A_tag_from_an_unknown_encoder_is_ignored()
    {
        // ffmpeg trusts the delay only from LAME and from its own encoders.
        Assert.Null(GaplessInfo.Read(new MemoryStream(FirstFrame(id3Size: 0, encoder: "GOGO1.0  "))));
    }

    [Fact]
    public void A_file_without_a_xing_tag_has_no_gapless_information()
    {
        byte[] frame = [0xFF, 0xF3, 0x88, 0xC0, .. new byte[200]];

        Assert.Null(GaplessInfo.Read(new MemoryStream(frame)));
    }

    [Fact]
    public void Trimming_drops_the_encoder_and_decoder_delay_and_the_padding()
    {
        // These are the values ffmpeg reports for a 16 kHz MP3 that its LAME wrapper encoded:
        // 1,105 samples skipped at the start and 389,020 samples in total.
        var (start, length) = Mp3Decoder.GetTrim(678 * 576, new GaplessInfo(576, 932, 678, 576));

        Assert.Equal(1105, start);
        Assert.Equal(389_020, length);
    }

    [Fact]
    public void Trimming_without_gapless_information_keeps_every_sample()
    {
        Assert.Equal((0, 1000), Mp3Decoder.GetTrim(1000, null));
    }

    [Fact]
    public void Trimming_never_runs_past_the_decoded_samples()
    {
        var (start, length) = Mp3Decoder.GetTrim(500, new GaplessInfo(576, 932, 678, 576));

        Assert.Equal(500, start);
        Assert.Equal(0, length);
    }

    private static byte[] FirstFrame(int id3Size, string encoder)
    {
        var bytes = new List<byte>();
        if (id3Size > 0)
        {
            bytes.AddRange("ID3"u8.ToArray());
            bytes.AddRange([4, 0, 0, 0, 0, 0, (byte)id3Size]);
            bytes.AddRange(new byte[id3Size]);
        }

        // MPEG-2 Layer III, 16 kHz, mono, no CRC: 9 bytes of side information follow the header.
        bytes.AddRange([0xFF, 0xF3, 0x88, 0xC0]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange("Info"u8.ToArray());
        var fields = new byte[4 + 4 + 4 + 100 + 4];
        BinaryPrimitives.WriteInt32BigEndian(fields, 0x0F);
        BinaryPrimitives.WriteInt32BigEndian(fields.AsSpan(4), 678);
        bytes.AddRange(fields);
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(encoder));
        bytes.AddRange(new byte[12]);

        // Delay 576 (0x240) and padding 932 (0x3A4), 12 bits each.
        bytes.AddRange([0x24, 0x03, 0xA4]);
        bytes.AddRange(new byte[200]);
        return [.. bytes];
    }
}
