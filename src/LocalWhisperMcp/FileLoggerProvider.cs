using System.Globalization;
using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Writes log messages to one file per day in the logs directory.</summary>
/// <remarks>
/// Claude Desktop doesn't keep what a server writes to stderr, so without a file a user has nothing
/// to send when something fails. Several server processes can run at once (one per client), so
/// each message opens the file, appends one line, and closes it; every line names its process.
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>How long log files are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly string directory;
    private readonly Func<DateTimeOffset> now;
    private readonly Lock gate = new();
    private readonly int processId = Environment.ProcessId;

    /// <summary>Initializes a new instance of the <see cref="FileLoggerProvider"/> class.</summary>
    /// <param name="directory">The logs directory.</param>
    /// <param name="now">Returns the current local time; tests replace it.</param>
    public FileLoggerProvider(string directory, Func<DateTimeOffset>? now = null)
    {
        this.directory = directory;
        this.now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Gets the path of today's log file.</summary>
    public string CurrentPath => Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"local-whisper-mcp-{now():yyyy-MM-dd}.log"));

    /// <summary>Deletes log files older than <see cref="Retention"/>.</summary>
    public void DeleteOldFiles()
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = now() - Retention;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("local-whisper-mcp-*.log"))
            {
                if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another process may be cleaning up too; the next start tries again.
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{now():yyyy-MM-dd HH:mm:ss.fff} [{processId}] {Abbreviate(level)} {category}: {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}");
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                using var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // Logging must never break a transcription.
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        _ => "crit",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
