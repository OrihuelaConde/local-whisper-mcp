using System.Text.Json;
using System.Text.Json.Serialization;
using LocalWhisperMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// The Claude Desktop extension passes the folders the user picked as arguments, because its
// settings expand a list of folders only into arguments, not into environment variables.
var warnings = new List<string>();
var settings = ServerSettings.FromEnvironment(args is ["--allowed-roots", .. var roots] ? roots : [], warnings);

switch (args)
{
    case [] or ["--allowed-roots", ..]:
        break;
    case ["--version" or "-v"]:
        Console.WriteLine(ServerInfo.Version);
        return 0;
    case ["download", .. var models]:
        return await ModelDownloadCommand.RunAsync(models, settings, warnings);
    case ["--help" or "-h"]:
        Console.WriteLine(Usage());
        return 0;
    default:
        Console.Error.WriteLine(Usage());
        return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // MCP clients start the server from any directory; don't read appsettings files from there.
    ContentRootPath = AppContext.BaseDirectory,
});

// The stdio transport owns stdout, so every log line goes to stderr, where MCP clients keep it.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
    options.ColorBehavior = LoggerColorBehavior.Disabled;
});
builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var fileLogs = new FileLoggerProvider(settings.LogsDirectory);
fileLogs.DeleteOldFiles();
builder.Logging.AddProvider(fileLogs);

// The SDK and the host log every request and lifetime event; the server's own messages are the useful ones.
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);

// Explicit registration instead of WithToolsFromAssembly: assembly scanning relies on reflection
// that trimming can't analyze.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
serializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(services => new ModelStore(settings.ModelsDirectory, services.GetRequiredService<ILogger<ModelStore>>()));
builder.Services.AddSingleton<WhisperHost>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = ServerInfo.Name, Title = "Local Whisper", Version = ServerInfo.Version };
        options.ServerInstructions = ServerInfo.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<TranscriptionTools>(serializerOptions);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
foreach (var warning in warnings)
{
    logger.LogWarning("{Warning}", warning);
}

Inbox.Prepare(settings.InboxDirectory, logger);

logger.LogInformation(
    "{Name} {Version} started. Models: {Models}. Default model: {Model}. Runtimes: {Runtimes}.",
    ServerInfo.Name,
    ServerInfo.Version,
    settings.ModelsDirectory,
    settings.DefaultModel,
    string.Join(", ", NativeRuntime.GetInstalled()));
await app.RunAsync();
return 0;

static string Usage() =>
    $"""
    {ServerInfo.Name} {ServerInfo.Version}: an MCP server that transcribes audio locally with Whisper.

    Usage:
      {ServerInfo.Name} [--allowed-roots DIR...]
                                           Run the MCP server over stdio. The folders replace
                                           LOCAL_WHISPER_ALLOWED_ROOTS.
      {ServerInfo.Name} download [MODEL]   Download a model and the VAD model. MODEL defaults to {ServerSettings.DefaultModelName}.
      {ServerInfo.Name} --version          Print the version.

    Settings come from environment variables: LOCAL_WHISPER_MODELS_DIR, LOCAL_WHISPER_MODEL,
    LOCAL_WHISPER_LANGUAGE, LOCAL_WHISPER_RUNTIME, LOCAL_WHISPER_USE_GPU, LOCAL_WHISPER_IDLE_MINUTES,
    LOCAL_WHISPER_ALLOWED_ROOTS, LOCAL_WHISPER_INBOX_DIR, LOCAL_WHISPER_THREADS, and
    LOCAL_WHISPER_AUTO_DOWNLOAD.
    """;

/// <summary>Serializes the tool results under Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostStatus))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
