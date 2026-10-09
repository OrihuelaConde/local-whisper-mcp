using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Manages the inbox: the directory where clients drop audio that the server deletes after transcribing.</summary>
internal static class Inbox
{
    /// <summary>The age after which a file left in the inbox, for example by a failed call, is deleted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>Creates the inbox if it's missing and deletes files older than <see cref="MaxAge"/>.</summary>
    /// <param name="directory">The inbox directory.</param>
    /// <param name="logger">The logger for deleted files and errors.</param>
    /// <remarks>Clients ask the user for access to the inbox, which works only if the directory exists.</remarks>
    public static void Prepare(string directory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var cutoff = DateTime.UtcNow - MaxAge;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    Delete(file.FullName, logger);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Couldn't prepare the inbox {Directory}.", directory);
        }
    }

    /// <summary>Deletes a transcribed file if it's in the inbox.</summary>
    /// <param name="fullPath">The full path of the transcribed file.</param>
    /// <param name="directory">The inbox directory.</param>
    /// <param name="logger">The logger for deleted files and errors.</param>
    /// <returns><see langword="true"/> if the file was in the inbox and is gone.</returns>
    /// <remarks>Files outside the inbox are never deleted.</remarks>
    public static bool DeleteIfInside(string fullPath, string directory, ILogger logger) =>
        AudioPathPolicy.IsInside(fullPath, [directory]) && Delete(fullPath, logger);

    private static bool Delete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
            logger.LogInformation("Deleted a file from the inbox.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Couldn't delete {File} from the inbox.", path);
            return false;
        }
    }
}
