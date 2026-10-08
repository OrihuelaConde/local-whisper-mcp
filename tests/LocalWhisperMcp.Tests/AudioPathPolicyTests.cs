using ModelContextProtocol;

namespace LocalWhisperMcp.Tests;

public sealed class AudioPathPolicyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("lwm-roots-").FullName;

    [Fact]
    public void A_file_inside_a_root_resolves_to_its_full_path()
    {
        var file = CreateFile("notes", "voice.ogg");
        var unnormalized = Path.Combine(root, "notes", "..", "notes", "voice.ogg");

        Assert.Equal(file, AudioPathPolicy.Resolve(unnormalized, [root]));
    }

    [Fact]
    public void A_relative_path_is_rejected()
    {
        var exception = Assert.Throws<McpException>(() => AudioPathPolicy.Resolve(Path.Combine("notes", "voice.ogg"), [root]));

        Assert.Contains("absolute", exception.Message);
    }

    [Fact]
    public void A_file_outside_the_roots_is_rejected_and_the_message_lists_the_roots()
    {
        var allowed = Path.Combine(root, "allowed");
        Directory.CreateDirectory(allowed);
        var file = CreateFile("other", "voice.ogg");

        var exception = Assert.Throws<McpException>(() => AudioPathPolicy.Resolve(file, [allowed]));

        Assert.Contains(allowed, exception.Message);
        Assert.Contains("LOCAL_WHISPER_ALLOWED_ROOTS", exception.Message);
    }

    [Fact]
    public void A_sibling_folder_that_shares_the_root_prefix_is_outside()
    {
        var file = CreateFile("database", "voice.ogg");

        Assert.Throws<McpException>(() => AudioPathPolicy.Resolve(file, [Path.Combine(root, "data")]));
    }

    [Fact]
    public void Dot_segments_cant_climb_out_of_a_root()
    {
        var allowed = Path.Combine(root, "allowed");
        Directory.CreateDirectory(allowed);
        CreateFile("secret.wav");

        Assert.Throws<McpException>(() => AudioPathPolicy.Resolve(Path.Combine(allowed, "..", "secret.wav"), [allowed]));
    }

    [Fact]
    public void A_missing_file_is_reported()
    {
        var exception = Assert.Throws<McpException>(() => AudioPathPolicy.Resolve(Path.Combine(root, "missing.wav"), [root]));

        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public void A_root_that_ends_with_a_separator_still_matches()
    {
        var drive = Path.GetPathRoot(root)!;

        Assert.True(AudioPathPolicy.IsInside(Path.Combine(root, "a.wav"), [drive]));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string CreateFile(params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }
}
