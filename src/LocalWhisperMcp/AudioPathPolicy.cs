using ModelContextProtocol;

namespace LocalWhisperMcp;

/// <summary>Checks that an audio path is absolute, exists, and lies inside an allowed root.</summary>
internal static class AudioPathPolicy
{
    /// <summary>Resolves an audio path against the allowed roots.</summary>
    /// <param name="path">The path that the client sent.</param>
    /// <param name="allowedRoots">The directories that the server may read from.</param>
    /// <returns>The full path of the file.</returns>
    /// <exception cref="McpException">The path is relative, outside the allowed roots, or missing.</exception>
    public static string Resolve(string path, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new McpException($"The path must be absolute: '{path}'.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!IsInside(fullPath, allowedRoots))
        {
            throw new McpException(OutsideRootsMessage(fullPath, allowedRoots));
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new McpException($"File not found: {fullPath}");
        }

        // A link inside an allowed root mustn't lead to a file outside of them.
        if (file.ResolveLinkTarget(returnFinalTarget: true) is { } target && !IsInside(target.FullName, allowedRoots))
        {
            throw new McpException(OutsideRootsMessage(fullPath, allowedRoots));
        }

        return fullPath;
    }

    /// <summary>Checks whether a full path lies inside one of the roots.</summary>
    /// <param name="fullPath">The full path.</param>
    /// <param name="roots">The full paths of the roots.</param>
    /// <returns><see langword="true"/> if the path is inside a root.</returns>
    public static bool IsInside(string fullPath, IReadOnlyList<string> roots)
    {
        // Windows and macOS file systems ignore case by default.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(root =>
        {
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, comparison);
        });
    }

    private static string OutsideRootsMessage(string fullPath, IReadOnlyList<string> roots) =>
        $"{fullPath} is outside the folders this server may read: {string.Join(", ", roots)}. " +
        "Copy the file into one of them, or add its folder to LOCAL_WHISPER_ALLOWED_ROOTS.";
}
