using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWhisperMcp.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private static readonly byte[] Content = [.. Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7))];
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"lwm-models-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_missing_model_downloads_through_the_redirect_and_is_verified()
    {
        var handler = new FakeHuggingFace(Content, Convert.ToHexStringLower(SHA256.HashData(Content)));
        using var store = new ModelStore(directory, NullLogger.Instance, handler);

        var path = await store.Ensure("ggml-base.bin").Completion;

        Assert.Equal(Path.Combine(directory, "ggml-base.bin"), path);
        Assert.Equal(Content, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", "https://cdn.example/ggml-base.bin"], handler.Requests);
        Assert.Equal(["base"], store.GetInstalledModels());
        Assert.Empty(Directory.GetFiles(directory, "*.download"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_hash_leaves_no_file()
    {
        var handler = new FakeHuggingFace(Content, new string('0', 64));
        using var store = new ModelStore(directory, NullLogger.Instance, handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.Ensure("ggml-base.bin").Completion);

        Assert.Contains("SHA-256", exception.Message);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task A_failed_download_is_retried_on_the_next_call()
    {
        var handler = new FakeHuggingFace(Content, new string('0', 64));
        using var store = new ModelStore(directory, NullLogger.Instance, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Ensure("ggml-base.bin").Completion);

        handler.Hash = Convert.ToHexStringLower(SHA256.HashData(Content));
        await store.Ensure("ggml-base.bin").Completion;

        Assert.True(File.Exists(Path.Combine(directory, "ggml-base.bin")));
    }

    [Fact]
    public async Task An_installed_model_doesnt_touch_the_network()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "ggml-my-finetune.bin"), [1], TestContext.Current.CancellationToken);
        var handler = new FakeHuggingFace(Content, null);
        using var store = new ModelStore(directory, NullLogger.Instance, handler);

        var download = store.Ensure("ggml-my-finetune.bin");

        Assert.True(download.Completion.IsCompletedSuccessfully);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void An_unknown_model_that_isnt_installed_cant_be_downloaded()
    {
        using var store = new ModelStore(directory, NullLogger.Instance, new FakeHuggingFace(Content, null));

        Assert.Throws<InvalidOperationException>(() => store.Ensure("ggml-my-finetune.bin"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Answers like Hugging Face: a redirect with the file's hash and size, then the file from a CDN.</summary>
    private sealed class FakeHuggingFace(byte[] content, string? hash) : HttpMessageHandler
    {
        public string? Hash { get; set; } = hash;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsoluteUri);
            if (uri.Host == "huggingface.co")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri($"https://cdn.example/{uri.Segments[^1]}");
                if (Hash is not null)
                {
                    redirect.Headers.Add("X-Linked-ETag", $"\"{Hash}\"");
                }

                redirect.Headers.Add("X-Linked-Size", content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
