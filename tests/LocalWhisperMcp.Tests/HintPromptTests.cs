namespace LocalWhisperMcp.Tests;

public sealed class HintPromptTests
{
    [Fact]
    public void Hints_become_a_comma_separated_sentence()
    {
        Assert.Equal("Ana Orihuela, Whisper.net, MCPB.", HintPrompt.Build(["Ana Orihuela", "Whisper.net", "MCPB"]));
    }

    [Fact]
    public void Blank_repeated_and_padded_hints_are_cleaned_up()
    {
        Assert.Equal("Concentus, Ogg Opus.", HintPrompt.Build(["  Concentus ", "", "concentus", "Ogg\n  Opus,", "   "]));
    }

    [Fact]
    public void No_hints_make_no_prompt()
    {
        Assert.Null(HintPrompt.Build(null));
        Assert.Null(HintPrompt.Build([]));
        Assert.Null(HintPrompt.Build([" ", ""]));
    }

    [Fact]
    public void A_final_period_is_not_doubled()
    {
        Assert.Equal("Acme S.A.", HintPrompt.Build(["Acme S.A."]));
    }

    [Fact]
    public void Hints_longer_than_the_limit_are_rejected()
    {
        var hints = Enumerable.Range(0, 60).Select(i => $"Nombre{i:00} Apellido").ToArray();
        var exception = Assert.Throws<ArgumentException>(() => HintPrompt.Build(hints));
        Assert.Contains("characters", exception.Message);
    }
}
