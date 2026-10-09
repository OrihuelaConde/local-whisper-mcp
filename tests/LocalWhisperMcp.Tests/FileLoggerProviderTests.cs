using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 30, 0, TimeSpan.FromHours(-3));
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"lwm-logs-{Guid.NewGuid():N}");

    [Fact]
    public void Messages_go_to_todays_file_with_time_process_level_and_category()
    {
        using var provider = new FileLoggerProvider(directory, () => Now);
        var logger = provider.CreateLogger("LocalWhisperMcp.Test");

        logger.LogInformation("Loaded {Model}.", "base");
        logger.LogError(new InvalidOperationException("boom"), "Failed.");

        Assert.Equal(Path.Combine(directory, "local-whisper-mcp-2026-10-09.log"), provider.CurrentPath);
        var text = File.ReadAllText(provider.CurrentPath);
        Assert.Contains($"2026-10-09 14:30:00.000 [{Environment.ProcessId}] info LocalWhisperMcp.Test: Loaded base.", text);
        Assert.Contains("fail LocalWhisperMcp.Test: Failed.", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void Files_older_than_a_week_are_deleted()
    {
        Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "local-whisper-mcp-2026-09-30.log");
        var recent = Path.Combine(directory, "local-whisper-mcp-2026-10-08.log");
        var other = Path.Combine(directory, "notes.log");
        foreach (var file in (string[])[old, recent, other])
        {
            File.WriteAllText(file, "x");
        }

        File.SetLastWriteTimeUtc(old, (Now - TimeSpan.FromDays(9)).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, (Now - TimeSpan.FromDays(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(other, (Now - TimeSpan.FromDays(30)).UtcDateTime);

        new FileLoggerProvider(directory, () => Now).DeleteOldFiles();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
