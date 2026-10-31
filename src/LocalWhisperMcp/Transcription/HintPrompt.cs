using System.Text.RegularExpressions;

namespace LocalWhisperMcp;

/// <summary>Turns the names and terms that a caller expects in the audio into a Whisper prompt.</summary>
/// <remarks>
/// Whisper reads the prompt as if it were the transcript of the audio before the recording, so a
/// list of names nudges it toward those spellings. The server passes the prompt to every 30-second
/// window. On recordings of several minutes, longer prompts made Whisper fall into repetition loops
/// more often; prompts of up to 120 characters kept most of the gain in names with fewer loops
/// (see <c>poc/HINTS-RESULTS.md</c>).
/// </remarks>
internal static partial class HintPrompt
{
    /// <summary>The longest prompt, in characters, that the hints may make.</summary>
    public const int MaxLength = 120;

    /// <summary>Builds the prompt from the hints.</summary>
    /// <param name="hints">The names and terms, in order of importance.</param>
    /// <returns>The prompt, such as <c>Ana Orihuela, Whisper.net, MCPB.</c>, or <see langword="null"/> if there are no hints.</returns>
    /// <exception cref="ArgumentException">The prompt would be longer than <see cref="MaxLength"/>.</exception>
    public static string? Build(IEnumerable<string>? hints)
    {
        if (hints is null)
        {
            return null;
        }

        // The tool's schema lets a client send null items, so they're skipped like blank ones.
        var terms = hints
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Select(hint => Whitespace().Replace(hint, " ").Trim().TrimEnd(',', ';'))
            .Where(hint => hint.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0)
        {
            return null;
        }

        var prompt = string.Join(", ", terms);
        if (!prompt.EndsWith('.'))
        {
            prompt += ".";
        }

        if (prompt.Length > MaxLength)
        {
            throw new ArgumentException(
                $"The hints add up to {prompt.Length} characters, and the server takes up to {MaxLength}. Keep only the names and terms most likely to be misspelled.",
                nameof(hints));
        }

        return prompt;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
