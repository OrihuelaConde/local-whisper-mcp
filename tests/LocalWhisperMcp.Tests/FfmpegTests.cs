namespace LocalWhisperMcp.Tests;

public sealed class FfmpegTests : IDisposable
{
    private readonly string? originalSetting = Environment.GetEnvironmentVariable("LOCAL_WHISPER_FFMPEG");
    private readonly string file = Path.Combine(Path.GetTempPath(), $"lwm-ffmpeg-{Guid.NewGuid():N}");

    [Fact]
    public void The_configured_executable_comes_first()
    {
        File.WriteAllBytes(file, [0]);
        Environment.SetEnvironmentVariable("LOCAL_WHISPER_FFMPEG", file);

        Assert.Equal(file, Ffmpeg.Find());
    }

    [Theory]
    [InlineData("${user_config.ffmpeg}")]
    [InlineData("")]
    public void An_unsaved_or_empty_setting_falls_back_to_the_search(string setting)
    {
        Environment.SetEnvironmentVariable("LOCAL_WHISPER_FFMPEG", setting);

        Assert.NotEqual(setting, Ffmpeg.Find());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCAL_WHISPER_FFMPEG", originalSetting);
        File.Delete(file);
    }
}
