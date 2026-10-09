using System.Text.RegularExpressions;

namespace LocalWhisperMcp;

/// <summary>Validates language codes before they reach whisper.cpp.</summary>
internal static partial class Languages
{
    /// <summary>Checks the shape of a language code.</summary>
    /// <param name="language">A lowercase code such as <c>es</c> or <c>yue</c>, or <c>auto</c>.</param>
    /// <returns><see langword="true"/> if the value is <c>auto</c> or two or three lowercase letters.</returns>
    /// <remarks>Whisper decides whether it knows the language; this check only rejects malformed values.</remarks>
    public static bool IsWellFormed(string language) => CodePattern().IsMatch(language);

    [GeneratedRegex("^(auto|[a-z]{2,3})$")]
    private static partial Regex CodePattern();
}
