using System.Globalization;

namespace LocalWhisperMcp;

/// <summary>Holds the server settings, read from environment variables.</summary>
internal sealed record ServerSettings
{
    /// <summary>The model that calls use when they don't name one.</summary>
    public const string DefaultModelName = "large-v3-turbo-q8_0";

    /// <summary>Gets the directory that contains the ggml model files.</summary>
    public required string ModelsDirectory { get; init; }

    /// <summary>Gets the model name to use when a call doesn't specify one.</summary>
    public required string DefaultModel { get; init; }

    /// <summary>Gets the language to use when a call doesn't specify one: a language code, or <c>auto</c>.</summary>
    public required string DefaultLanguage { get; init; }

    /// <summary>Gets the preferred native runtime: <c>auto</c>, <c>cpu</c>, <c>vulkan</c>, or <c>cuda</c>.</summary>
    public required string Runtime { get; init; }

    /// <summary>Gets the time without calls after which the model is released.</summary>
    public required TimeSpan IdleTimeout { get; init; }

    /// <summary>Gets the directories that the server may read audio from, besides the inbox.</summary>
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    /// <summary>Gets the directory where clients drop audio for the server, which deletes each file after transcribing it.</summary>
    /// <remarks>
    /// A cloud session can't hand its attachments to the server directly: it copies them to the
    /// computer first. The inbox gives that copy a fixed place that the server cleans up, because
    /// the session itself can't delete files without asking the user again.
    /// </remarks>
    public required string InboxDirectory { get; init; }

    /// <summary>Gets every directory that the server may read audio from: the allowed roots and the inbox.</summary>
    public IReadOnlyList<string> ReadableRoots => [.. AllowedRoots, InboxDirectory];

    /// <summary>Gets the number of CPU threads to use.</summary>
    public required int Threads { get; init; }

    /// <summary>Gets a value indicating whether missing models are downloaded on first use.</summary>
    public required bool AutoDownload { get; init; }

    /// <summary>Reads the settings from the process environment.</summary>
    /// <param name="warnings">Receives a message for every variable with an invalid value.</param>
    /// <returns>The settings, with defaults for every variable that isn't set or isn't valid.</returns>
    public static ServerSettings FromEnvironment(ICollection<string> warnings) =>
        FromVariables(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), warnings);

    /// <summary>Reads the settings from a set of variables.</summary>
    /// <param name="getVariable">Returns the value of a variable, or <see langword="null"/> if it isn't set.</param>
    /// <param name="home">The user's home directory.</param>
    /// <param name="warnings">Receives a message for every variable with an invalid value.</param>
    /// <returns>The settings, with defaults for every variable that isn't set or isn't valid.</returns>
    /// <remarks>
    /// An invalid value falls back to the default instead of stopping the server, because clients
    /// such as Claude Desktop only report that a server failed to start, not why.
    /// </remarks>
    public static ServerSettings FromVariables(Func<string, string?> getVariable, string home, ICollection<string> warnings)
    {
        // Not LocalApplicationData: on Windows, MSIX-packaged clients such as Claude Desktop see a
        // virtualized AppData, so models stored there by other processes are invisible to the server.
        var modelsDirectory = GetDirectory("LOCAL_WHISPER_MODELS_DIR", Path.Combine(home, ".local-whisper-mcp", "models"));
        var inboxDirectory = GetDirectory("LOCAL_WHISPER_INBOX_DIR", Path.Combine(home, ".local-whisper-mcp", "inbox"));

        var defaultModel = Get("LOCAL_WHISPER_MODEL") ?? DefaultModelName;
        if (!ModelCatalog.IsValidName(defaultModel))
        {
            warnings.Add($"LOCAL_WHISPER_MODEL '{defaultModel}' isn't a valid model name; using {DefaultModelName}.");
            defaultModel = DefaultModelName;
        }

        var language = (Get("LOCAL_WHISPER_LANGUAGE") ?? "auto").ToLowerInvariant();
        if (!Languages.IsWellFormed(language))
        {
            warnings.Add($"LOCAL_WHISPER_LANGUAGE '{language}' isn't a language code; using auto.");
            language = "auto";
        }

        var runtime = (Get("LOCAL_WHISPER_RUNTIME") ?? "auto").ToLowerInvariant();
        if (runtime is not ("auto" or "cpu" or "vulkan" or "cuda"))
        {
            warnings.Add($"LOCAL_WHISPER_RUNTIME '{runtime}' isn't auto, cpu, vulkan, or cuda; using auto.");
            runtime = "auto";
        }

        var idleMinutes = 10.0;
        if (Get("LOCAL_WHISPER_IDLE_MINUTES") is { } idle)
        {
            if (double.TryParse(idle, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 && parsed <= TimeSpan.MaxValue.TotalMinutes)
            {
                idleMinutes = parsed;
            }
            else
            {
                warnings.Add($"LOCAL_WHISPER_IDLE_MINUTES '{idle}' isn't a number of minutes; using {idleMinutes}.");
            }
        }

        var threads = Math.Min(Environment.ProcessorCount, 8);
        if (Get("LOCAL_WHISPER_THREADS") is { } threadsValue)
        {
            if (int.TryParse(threadsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                threads = parsed;
            }
            else
            {
                warnings.Add($"LOCAL_WHISPER_THREADS '{threadsValue}' isn't a positive integer; using {threads}.");
            }
        }

        var autoDownload = true;
        if (Get("LOCAL_WHISPER_AUTO_DOWNLOAD") is { } download)
        {
            switch (download.ToLowerInvariant())
            {
                case "true" or "1" or "yes":
                    autoDownload = true;
                    break;
                case "false" or "0" or "no":
                    autoDownload = false;
                    break;
                default:
                    warnings.Add($"LOCAL_WHISPER_AUTO_DOWNLOAD '{download}' isn't true or false; using true.");
                    break;
            }
        }

        var roots = new List<string>();
        foreach (var root in (Get("LOCAL_WHISPER_ALLOWED_ROOTS") ?? home).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = ExpandHome(root, home);
            if (Path.IsPathFullyQualified(expanded))
            {
                roots.Add(Path.GetFullPath(expanded));
            }
            else
            {
                warnings.Add($"LOCAL_WHISPER_ALLOWED_ROOTS ignores '{root}' because it isn't an absolute path.");
            }
        }

        if (roots.Count == 0)
        {
            warnings.Add($"LOCAL_WHISPER_ALLOWED_ROOTS has no absolute paths; allowing {home}.");
            roots.Add(Path.GetFullPath(home));
        }

        return new ServerSettings
        {
            ModelsDirectory = modelsDirectory,
            DefaultModel = defaultModel,
            DefaultLanguage = language,
            Runtime = runtime,
            IdleTimeout = TimeSpan.FromMinutes(idleMinutes),
            AllowedRoots = roots,
            InboxDirectory = inboxDirectory,
            Threads = threads,
            AutoDownload = autoDownload,
        };

        string? Get(string name) => getVariable(name) is { } value && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        string GetDirectory(string name, string fallback)
        {
            var directory = Get(name) is { } value ? ExpandHome(value, home) : fallback;
            if (Path.IsPathFullyQualified(directory))
            {
                return Path.GetFullPath(directory);
            }

            warnings.Add($"{name} must be an absolute path; using {fallback}.");
            return Path.GetFullPath(fallback);
        }
    }

    // Shells expand ~, but MCP clients pass environment variables verbatim.
    private static string ExpandHome(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal) ? Path.Combine(home, path[2..])
        : path;
}
