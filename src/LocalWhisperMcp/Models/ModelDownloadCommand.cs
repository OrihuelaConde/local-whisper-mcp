using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace LocalWhisperMcp;

/// <summary>Implements the <c>download</c> command, which fetches models before the first transcription.</summary>
internal static class ModelDownloadCommand
{
    /// <summary>Downloads the requested Whisper models and the VAD model.</summary>
    /// <param name="names">The model names; empty for the default model.</param>
    /// <param name="settings">The server settings.</param>
    /// <param name="warnings">The settings warnings to print first.</param>
    /// <returns>The process exit code: 0 on success, 1 if a download failed, or 2 for an unknown model.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> names, ServerSettings settings, IReadOnlyList<string> warnings)
    {
        using var loggerFactory = LoggerFactory.Create(logging => logging
            .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Disabled;
            }));
        var logger = loggerFactory.CreateLogger("download");
        foreach (var warning in warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        var models = names.Count == 0 ? [settings.DefaultModel] : names.Select(name => ModelCatalog.Normalize(name) ?? name).ToArray();
        if (models.FirstOrDefault(name => !ModelCatalog.KnownModels.Contains(name)) is { } unknown)
        {
            Console.Error.WriteLine($"Unknown model '{unknown}'. Models: {string.Join(", ", ModelCatalog.KnownModels)}.");
            return 2;
        }

        using var store = new ModelStore(settings.ModelsDirectory, logger);
        var exitCode = 0;
        foreach (var fileName in models.Select(ModelCatalog.GetFileName).Append(ModelCatalog.VadModelFileName))
        {
            var download = store.Ensure(fileName);
            while (!download.Completion.IsCompleted)
            {
                await Task.WhenAny(download.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
                if (!download.Completion.IsCompleted && download.GetStatus() is { Percent: { } percent } status)
                {
                    logger.LogInformation("{File}: {Percent:F0}% of {Megabytes:F0} MB.", fileName, percent, status.TotalBytes / 1e6);
                }
            }

            try
            {
                // Standard output carries only the paths, so scripts can use them.
                Console.WriteLine(await download.Completion);
            }
            catch (Exception)
            {
                // The store already logged the error.
                exitCode = 1;
            }
        }

        return exitCode;
    }
}
