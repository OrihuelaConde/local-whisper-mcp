using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Finds model files in the models directory and downloads the missing ones.</summary>
/// <remarks>
/// A download belongs to the store, not to the call that started it: if the MCP client gives up on
/// a call while a large model downloads, the download continues and the next call finds it.
/// </remarks>
internal sealed class ModelStore : IDisposable
{
    private readonly HttpClient http;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<string, ModelDownload> downloads = [];

    /// <summary>Initializes a new instance of the <see cref="ModelStore"/> class.</summary>
    /// <param name="modelsDirectory">The directory that contains the model files.</param>
    /// <param name="logger">The logger for download events.</param>
    /// <param name="handler">The HTTP handler, or <see langword="null"/> to use the network.</param>
    public ModelStore(string modelsDirectory, ILogger logger, HttpMessageHandler? handler = null)
    {
        ModelsDirectory = modelsDirectory;
        this.logger = logger;

        // Redirects are followed by hand: Hugging Face sends the file's SHA-256 only on the first
        // response, which redirects to a CDN.
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"local-whisper-mcp/{ServerInfo.Version}");
    }

    /// <summary>Gets the directory that contains the model files.</summary>
    public string ModelsDirectory { get; }

    /// <summary>Gets the path where a model file is, or would be once downloaded.</summary>
    /// <param name="fileName">The model file name.</param>
    /// <returns>The full path.</returns>
    public string GetPath(string fileName) => Path.Combine(ModelsDirectory, fileName);

    /// <summary>Lists the Whisper models in the models directory.</summary>
    /// <returns>The model names, sorted.</returns>
    public IReadOnlyList<string> GetInstalledModels()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(ModelsDirectory, "ggml-*.bin")
            .Select(path => ModelCatalog.GetModelName(Path.GetFileName(path)))
            .OfType<string>()
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Lists the downloads in progress.</summary>
    /// <returns>One entry per file being downloaded.</returns>
    public IReadOnlyList<DownloadStatus> GetDownloads()
    {
        lock (gate)
        {
            return [.. downloads.Values
                .Where(d => !d.Completion.IsCompleted)
                .Select(d => d.GetStatus())];
        }
    }

    /// <summary>Gets the model file, starting a download if it's missing.</summary>
    /// <param name="fileName">The model file name, which must be a known model.</param>
    /// <returns>The download, already completed if the file exists.</returns>
    /// <exception cref="InvalidOperationException">The file isn't installed and isn't a known model.</exception>
    public ModelDownload Ensure(string fileName)
    {
        var path = GetPath(fileName);
        lock (gate)
        {
            if (downloads.TryGetValue(fileName, out var running) && !running.Completion.IsCompleted)
            {
                return running;
            }

            if (File.Exists(path))
            {
                return ModelDownload.Completed(fileName, path);
            }

            var uri = ModelCatalog.GetDownloadUri(fileName) ?? throw new InvalidOperationException($"{fileName} isn't a known model.");
            var download = new ModelDownload(fileName);
            downloads[fileName] = download;
            download.Start(() => DownloadAsync(uri, path, download, stopping.Token));
            return download;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
        http.Dispose();
    }

    private async Task<string> DownloadAsync(Uri uri, string path, ModelDownload download, CancellationToken cancellationToken)
    {
        var partial = path + ".download";
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Downloading {File} from {Uri}.", download.FileName, uri);
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            string? expectedHash = null;
            long? expectedSize = null;
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            for (var redirects = 0; IsRedirect(response.StatusCode); redirects++)
            {
                expectedHash ??= GetLinkedHash(response);
                expectedSize ??= GetLinkedSize(response);
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 10)
                {
                    throw new HttpRequestException($"Too many redirects or a redirect without a location from {uri}.");
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                expectedHash ??= GetLinkedHash(response);
                expectedSize ??= GetLinkedSize(response) ?? response.Content.Headers.ContentLength;
                download.TotalBytes = expectedSize;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = OpenPartial(partial))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        download.BytesReceived += read;
                    }
                }

                if (expectedSize is { } size && download.BytesReceived != size)
                {
                    throw new InvalidDataException($"The download of {download.FileName} ended after {download.BytesReceived} of {size} bytes.");
                }

                var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (expectedHash is not null && actualHash != expectedHash)
                {
                    throw new InvalidDataException($"The SHA-256 of {download.FileName} is {actualHash}, but Hugging Face lists {expectedHash}.");
                }
            }

            File.Move(partial, path, overwrite: true);
            logger.LogInformation("Downloaded {File} ({Megabytes:F0} MB) in {Seconds:F1} s.", download.FileName, download.BytesReceived / 1e6, stopwatch.Elapsed.TotalSeconds);
            return path;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to download {File}.", download.FileName);
            TryDelete(partial);
            throw;
        }
    }

    private static FileStream OpenPartial(string partial)
    {
        try
        {
            // FileShare.None makes a second server process that downloads the same model fail here
            // instead of interleaving writes into the same file.
            return new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        }
        catch (IOException exception)
        {
            throw new IOException($"Another process is writing {partial}. Wait for it to finish and try again.", exception);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    // Hugging Face serves model files through Git LFS and reports their SHA-256 and size in these
    // headers.
    private static string? GetLinkedHash(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Linked-ETag", out var values) && values.FirstOrDefault()?.Trim('"', ' ') is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)
            ? hash.ToLowerInvariant()
            : null;

    private static long? GetLinkedSize(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Linked-Size", out var values) && long.TryParse(values.FirstOrDefault(), out var size) ? size : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Another process still has it open; its own download will replace it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Tracks the download of one model file.</summary>
internal sealed class ModelDownload
{
    private long bytesReceived;
    private long totalBytes = -1;
    private Task<string>? completion;

    /// <summary>Initializes a new instance of the <see cref="ModelDownload"/> class.</summary>
    /// <param name="fileName">The model file name.</param>
    public ModelDownload(string fileName) => FileName = fileName;

    /// <summary>Gets the model file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the task that completes with the path of the downloaded file.</summary>
    public Task<string> Completion => completion ?? throw new InvalidOperationException("The download hasn't started.");

    /// <summary>Gets or sets the number of bytes written so far.</summary>
    public long BytesReceived
    {
        get => Interlocked.Read(ref bytesReceived);
        set => Interlocked.Exchange(ref bytesReceived, value);
    }

    /// <summary>Gets or sets the size of the file, or <see langword="null"/> while it's unknown.</summary>
    public long? TotalBytes
    {
        get => Interlocked.Read(ref totalBytes) is var total and >= 0 ? total : null;
        set => Interlocked.Exchange(ref totalBytes, value ?? -1);
    }

    /// <summary>Creates a download that has already completed.</summary>
    /// <param name="fileName">The model file name.</param>
    /// <param name="path">The path of the existing file.</param>
    /// <returns>The completed download.</returns>
    public static ModelDownload Completed(string fileName, string path) => new(fileName) { completion = Task.FromResult(path) };

    /// <summary>Starts the download on the thread pool.</summary>
    /// <param name="download">The function that downloads the file and returns its path.</param>
    public void Start(Func<Task<string>> download) => completion = Task.Run(download);

    /// <summary>Describes the progress of the download.</summary>
    /// <returns>The status.</returns>
    public DownloadStatus GetStatus()
    {
        var total = TotalBytes;
        var received = BytesReceived;
        return new DownloadStatus(FileName, received, total, total is > 0 ? Math.Round(100.0 * received / total.Value, 1) : null);
    }
}

/// <summary>Describes a model download in progress.</summary>
/// <param name="File">The model file name.</param>
/// <param name="BytesReceived">The bytes written so far.</param>
/// <param name="TotalBytes">The size of the file, or <see langword="null"/> while it's unknown.</param>
/// <param name="Percent">The percentage done, or <see langword="null"/> while the size is unknown.</param>
internal sealed record DownloadStatus(string File, long BytesReceived, long? TotalBytes, double? Percent);
