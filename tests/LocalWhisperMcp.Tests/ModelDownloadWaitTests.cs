using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace LocalWhisperMcp.Tests;

public sealed class ModelDownloadWaitTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"lwm-wait-{Guid.NewGuid():N}");
    private readonly SemaphoreSlim gate = new(0);

    [Fact]
    public async Task A_slow_download_returns_a_retry_message_and_keeps_going()
    {
        using var store = new ModelStore(directory, NullLogger.Instance, new GatedHandler(gate));
        using var host = CreateHost(store, autoDownload: true);
        var reports = new List<ProgressNotificationValue>();
        var progress = new SynchronousProgress(reports.Add);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => TranscriptionTools.EnsureModelAsync(host, "tiny", progress, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));

        Assert.Contains("still downloading", exception.Message);
        Assert.NotEmpty(reports);
        Assert.Equal(GatedHandler.Length, reports[^1].Total);
        Assert.Single(store.GetDownloads());

        gate.Release();
        var path = await TranscriptionTools.EnsureModelAsync(host, "tiny", progress, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(GatedHandler.Length, new FileInfo(path).Length);
        Assert.Empty(store.GetDownloads());
    }

    [Fact]
    public async Task A_missing_model_isnt_downloaded_when_downloads_are_off()
    {
        using var store = new ModelStore(directory, NullLogger.Instance, new GatedHandler(gate));
        using var host = CreateHost(store, autoDownload: false);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => TranscriptionTools.EnsureModelAsync(host, "tiny", new SynchronousProgress(_ => { }), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.Contains("local-whisper-mcp download tiny", exception.Message);
        Assert.False(Directory.Exists(directory));
    }

    public void Dispose()
    {
        gate.Release(10);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private WhisperHost CreateHost(ModelStore store, bool autoDownload)
    {
        var settings = ServerSettings.FromVariables(_ => null, Path.GetTempPath(), []) with { ModelsDirectory = directory, AutoDownload = autoDownload };
        return new WhisperHost(settings, store, NullLogger<WhisperHost>.Instance);
    }

    private sealed class SynchronousProgress(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => report(value);
    }

    /// <summary>Serves a model whose second half waits for the gate.</summary>
    private sealed class GatedHandler(SemaphoreSlim gate) : HttpMessageHandler
    {
        public const int Length = 2 << 20;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new GatedContent(gate) });

        private sealed class GatedContent(SemaphoreSlim gate) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                await stream.WriteAsync(new byte[Length / 2]);
                await stream.FlushAsync();
                await gate.WaitAsync();
                await stream.WriteAsync(new byte[Length / 2]);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = Length;
                return true;
            }
        }
    }
}
