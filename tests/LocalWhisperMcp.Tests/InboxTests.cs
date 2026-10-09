using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWhisperMcp.Tests;

public sealed class InboxTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("lwm-inbox-").FullName;

    private string InboxDirectory => Path.Combine(root, "inbox");

    [Fact]
    public void Preparing_creates_the_inbox_and_removes_only_files_older_than_a_day()
    {
        Directory.CreateDirectory(InboxDirectory);
        var stale = Path.Combine(InboxDirectory, "stale.ogg");
        var fresh = Path.Combine(InboxDirectory, "fresh.ogg");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(fresh, [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - Inbox.MaxAge - TimeSpan.FromMinutes(1));

        Inbox.Prepare(InboxDirectory, NullLogger.Instance);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Preparing_a_missing_inbox_creates_it()
    {
        Inbox.Prepare(InboxDirectory, NullLogger.Instance);

        Assert.True(Directory.Exists(InboxDirectory));
    }

    [Fact]
    public void A_transcribed_file_in_the_inbox_is_deleted()
    {
        Directory.CreateDirectory(InboxDirectory);
        var file = Path.Combine(InboxDirectory, "voice.ogg");
        File.WriteAllBytes(file, [1]);

        Assert.True(Inbox.DeleteIfInside(file, InboxDirectory, NullLogger.Instance));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_transcribed_file_outside_the_inbox_is_never_deleted()
    {
        // A sibling folder whose name starts like the inbox's mustn't count as the inbox.
        var outside = Path.Combine(root, "inbox-old", "voice.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, [1]);

        Assert.False(Inbox.DeleteIfInside(outside, InboxDirectory, NullLogger.Instance));
        Assert.True(File.Exists(outside));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
