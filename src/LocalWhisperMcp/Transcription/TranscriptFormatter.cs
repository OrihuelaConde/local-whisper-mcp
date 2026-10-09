using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalWhisperMcp;

/// <summary>Formats a transcript as plain text, SubRip, or JSON.</summary>
internal static class TranscriptFormatter
{
    // Spanish, Portuguese, and most other languages would otherwise come out as \u escapes, which
    // are harder to read and cost more tokens. The output isn't embedded in HTML.
    private static readonly TranscriptJsonContext Json = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });

    /// <summary>The plain-text transcript of audio without speech.</summary>
    public const string NoSpeech = "[no speech detected]";

    /// <summary>Gets the supported format names.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["txt", "srt", "json"];

    /// <summary>Formats a transcription in the requested format.</summary>
    /// <param name="result">The transcription.</param>
    /// <param name="format">One of <c>txt</c>, <c>srt</c>, or <c>json</c>.</param>
    /// <returns>The formatted transcript.</returns>
    /// <exception cref="ArgumentException">The format isn't supported.</exception>
    public static string Format(TranscriptionResult result, string format) => format switch
    {
        "txt" => result.Segments.Count == 0 ? NoSpeech : string.Join(' ', result.Segments.Select(s => s.Text)),
        "srt" => ToSrt(result.Segments),
        "json" => JsonSerializer.Serialize(
            new TranscriptJson(
                result.Language,
                Seconds(result.Duration),
                [.. result.Segments.Select(s => new SegmentJson(Seconds(s.Start), Seconds(s.End), s.Text))]),
            Json.TranscriptJson),
        _ => throw new ArgumentException($"Unknown format '{format}'.", nameof(format)),
    };

    private static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1}\n{Timestamp(segments[i].Start)} --> {Timestamp(segments[i].End)}\n{segments[i].Text}\n\n");
        }

        return builder.ToString();
    }

    private static string Timestamp(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");

    // Whisper timestamps have a resolution of 10 ms.
    private static double Seconds(TimeSpan value) => Math.Round(value.TotalSeconds, 2);
}

/// <summary>Represents a transcript in the JSON format.</summary>
/// <param name="Language">The language Whisper used, or <see langword="null"/> if there was no speech.</param>
/// <param name="Duration">The duration of the audio, in seconds.</param>
/// <param name="Segments">The transcribed segments.</param>
internal sealed record TranscriptJson(string? Language, double Duration, IReadOnlyList<SegmentJson> Segments);

/// <summary>Represents one segment in the JSON format.</summary>
/// <param name="Start">The start of the segment, in seconds from the beginning of the audio.</param>
/// <param name="End">The end of the segment, in seconds from the beginning of the audio.</param>
/// <param name="Text">The transcribed text.</param>
internal sealed record SegmentJson(double Start, double End, string Text);

[JsonSerializable(typeof(TranscriptJson))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext;
