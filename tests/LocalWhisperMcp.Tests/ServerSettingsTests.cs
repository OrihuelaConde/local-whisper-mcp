namespace LocalWhisperMcp.Tests;

public sealed class ServerSettingsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home");

    [Fact]
    public void Defaults_apply_when_no_variable_is_set()
    {
        var warnings = new List<string>();

        var settings = Read([], warnings);

        Assert.Empty(warnings);
        Assert.Equal(Path.Combine(Home, ".local-whisper-mcp", "models"), settings.ModelsDirectory);
        Assert.Equal("large-v3-turbo-q8_0", settings.DefaultModel);
        Assert.Equal("auto", settings.DefaultLanguage);
        Assert.Equal("auto", settings.Runtime);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.IdleTimeout);
        Assert.Equal([Home], settings.AllowedRoots);
        Assert.True(settings.AutoDownload);
        Assert.InRange(settings.Threads, 1, 8);
    }

    [Fact]
    public void Valid_values_override_the_defaults()
    {
        var models = Path.Combine(Path.GetTempPath(), "models");
        var audio = Path.Combine(Path.GetTempPath(), "audio");
        var warnings = new List<string>();

        var settings = Read(
            new()
            {
                ["LOCAL_WHISPER_MODELS_DIR"] = models,
                ["LOCAL_WHISPER_MODEL"] = "base",
                ["LOCAL_WHISPER_LANGUAGE"] = "ES",
                ["LOCAL_WHISPER_RUNTIME"] = "CPU",
                ["LOCAL_WHISPER_IDLE_MINUTES"] = "0.5",
                ["LOCAL_WHISPER_THREADS"] = "3",
                ["LOCAL_WHISPER_AUTO_DOWNLOAD"] = "false",
                ["LOCAL_WHISPER_ALLOWED_ROOTS"] = $" {audio} {Path.PathSeparator}~{Path.DirectorySeparatorChar}notes{Path.PathSeparator}",
            },
            warnings);

        Assert.Empty(warnings);
        Assert.Equal(models, settings.ModelsDirectory);
        Assert.Equal("base", settings.DefaultModel);
        Assert.Equal("es", settings.DefaultLanguage);
        Assert.Equal("cpu", settings.Runtime);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.IdleTimeout);
        Assert.Equal(3, settings.Threads);
        Assert.False(settings.AutoDownload);
        Assert.Equal([audio, Path.Combine(Home, "notes")], settings.AllowedRoots);
    }

    [Fact]
    public void Invalid_values_fall_back_to_the_defaults_with_a_warning()
    {
        var warnings = new List<string>();

        var settings = Read(
            new()
            {
                ["LOCAL_WHISPER_MODELS_DIR"] = "relative/models",
                ["LOCAL_WHISPER_MODEL"] = "../escape",
                ["LOCAL_WHISPER_LANGUAGE"] = "spanish",
                ["LOCAL_WHISPER_RUNTIME"] = "metal",
                ["LOCAL_WHISPER_IDLE_MINUTES"] = "ten",
                ["LOCAL_WHISPER_THREADS"] = "0",
                ["LOCAL_WHISPER_AUTO_DOWNLOAD"] = "maybe",
                ["LOCAL_WHISPER_ALLOWED_ROOTS"] = "relative",
            },
            warnings);

        Assert.Equal(9, warnings.Count);
        Assert.Equal(Path.Combine(Home, ".local-whisper-mcp", "models"), settings.ModelsDirectory);
        Assert.Equal("large-v3-turbo-q8_0", settings.DefaultModel);
        Assert.Equal("auto", settings.DefaultLanguage);
        Assert.Equal("auto", settings.Runtime);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.IdleTimeout);
        Assert.True(settings.AutoDownload);
        Assert.Equal([Home], settings.AllowedRoots);
    }

    [Fact]
    public void Blank_values_count_as_unset()
    {
        // Claude Desktop passes an empty string for an optional setting the user left empty.
        var warnings = new List<string>();

        var settings = Read(new() { ["LOCAL_WHISPER_MODEL"] = "", ["LOCAL_WHISPER_IDLE_MINUTES"] = "  " }, warnings);

        Assert.Empty(warnings);
        Assert.Equal("large-v3-turbo-q8_0", settings.DefaultModel);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.IdleTimeout);
    }

    private static ServerSettings Read(Dictionary<string, string> variables, List<string> warnings) =>
        ServerSettings.FromVariables(name => variables.GetValueOrDefault(name), Home, warnings);
}
