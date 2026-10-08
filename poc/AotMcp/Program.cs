using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// The stdio transport owns stdout, so every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Explicit registration instead of WithToolsFromAssembly: assembly scanning relies on
// reflection that trimming can't analyze.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
serializerOptions.TypeInfoResolverChain.Insert(0, PocJsonContext.Default);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PocTools>(serializerOptions);

await builder.Build().RunAsync();

/// <summary>Provides test tools that exercise the MCP SDK under Native AOT.</summary>
[McpServerToolType]
public sealed class PocTools
{
    /// <summary>Returns the message unchanged.</summary>
    /// <param name="message">The text to send back.</param>
    /// <returns>The same text that the caller sent.</returns>
    [McpServerTool(Name = "echo"), Description("Returns the message unchanged.")]
    public static string Echo([Description("The text to send back.")] string message) => message;

    /// <summary>Describes the process that runs the server.</summary>
    /// <returns>The operating system, architecture, and code generation mode of the process.</returns>
    [McpServerTool(Name = "runtime_info"), Description("Describes the process that runs the server.")]
    public static RuntimeInfo GetRuntimeInfo() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        RuntimeFeature.IsDynamicCodeSupported);
}

/// <summary>Describes the process that runs the server.</summary>
/// <param name="Os">The operating system description.</param>
/// <param name="RuntimeIdentifier">The runtime identifier (RID) of the process.</param>
/// <param name="Architecture">The process architecture.</param>
/// <param name="Framework">The .NET runtime description.</param>
/// <param name="IsDynamicCodeSupported"><see langword="false"/> when the process runs as Native AOT code.</param>
public sealed record RuntimeInfo(
    string Os,
    string RuntimeIdentifier,
    string Architecture,
    string Framework,
    bool IsDynamicCodeSupported);

[JsonSerializable(typeof(RuntimeInfo))]
internal sealed partial class PocJsonContext : JsonSerializerContext;
