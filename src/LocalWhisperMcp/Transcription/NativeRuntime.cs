using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace LocalWhisperMcp;

/// <summary>Chooses the whisper.cpp native runtime and reports which one, and which GPU, is in use.</summary>
/// <remarks>
/// This is the only place that knows about platform-specific runtimes. Every preference ends with
/// the CPU runtime, so a machine without a usable GPU runtime still transcribes.
/// </remarks>
internal static partial class NativeRuntime
{
    private static string? gpuBackend;
    private static string? gpuName;

    /// <summary>Gets the native runtime that Whisper.net loaded, such as <c>Cpu</c> or <c>Vulkan</c>, or <see langword="null"/> before the first model loads.</summary>
    public static string? Loaded => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Gets the device that runs the model: a GPU name, <c>CPU</c>, or <see langword="null"/> before the first model loads.</summary>
    public static string? Device => Loaded is null ? null : DescribeDevice(gpuName, gpuBackend);

    /// <summary>Describes the device from what whisper.cpp logged.</summary>
    /// <param name="name">The GPU name, or <see langword="null"/> if the log didn't name one.</param>
    /// <param name="backend">The GPU backend in use, such as <c>Vulkan0</c>, or <see langword="null"/> if the model runs on the CPU.</param>
    /// <returns>A description such as <c>NVIDIA GeForce RTX 3080 (Vulkan)</c> or <c>CPU</c>.</returns>
    /// <remarks>
    /// The API comes from the backend, not from the loaded runtime: on Apple Silicon, Metal ships
    /// inside the CPU runtime.
    /// </remarks>
    internal static string DescribeDevice(string? name, string? backend)
    {
        if (backend is null)
        {
            return "CPU";
        }

        var api = backend.TrimEnd("0123456789".ToCharArray()) switch
        {
            "MTL" => "Metal",
            var other => other,
        };
        return $"{name ?? backend} ({api})";
    }

    /// <summary>Selects the native runtimes that Whisper.net may load, in order of preference.</summary>
    /// <param name="preference">One of <c>auto</c>, <c>cpu</c>, <c>vulkan</c>, or <c>cuda</c>.</param>
    /// <remarks>Only takes effect before the first model loads.</remarks>
    public static void Configure(string preference)
    {
        RuntimeOptions.RuntimeLibraryOrder = preference switch
        {
            "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => RuntimeOptions.RuntimeLibraryOrder,
        };
    }

    /// <summary>Lists the native runtimes installed next to the executable, in the order Whisper.net tries them.</summary>
    /// <returns>The runtime names, such as <c>Vulkan</c> and <c>Cpu</c>.</returns>
    public static IReadOnlyList<string> GetInstalled()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var target = $"{platform}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        return [.. RuntimeOptions.RuntimeLibraryOrder
            .Where(library => Directory.Exists(library switch
            {
                RuntimeLibrary.Cpu => Path.Combine(runtimes, target),
                RuntimeLibrary.CpuNoAvx => Path.Combine(runtimes, "noavx", target),
                _ => Path.Combine(runtimes, library.ToString().ToLowerInvariant(), target),
            }))
            .Select(library => library.ToString())];
    }

    /// <summary>Forwards whisper.cpp log messages to a logger and watches them for the GPU in use.</summary>
    /// <param name="logger">The logger that receives the messages.</param>
    /// <returns>An object that stops the forwarding when disposed.</returns>
    /// <remarks>
    /// whisper.cpp reports the GPU only in its log, so the server reads the device name from there.
    /// The messages are verbose, so they go to the logger at the debug level.
    /// </remarks>
    public static IDisposable ForwardLogs(ILogger logger) => LogProvider.AddLogger((level, message) =>
    {
        if (message is null)
        {
            return;
        }

        Observe(message);
        var logLevel = level switch
        {
            WhisperLogLevel.Error => LogLevel.Error,
            WhisperLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Debug,
        };
        if (logger.IsEnabled(logLevel))
        {
            logger.Log(logLevel, "{Message}", message.TrimEnd());
        }
    });

    /// <summary>Reads the name of the first GPU from a whisper.cpp log message.</summary>
    /// <param name="message">The log message.</param>
    /// <returns>The GPU name, or <see langword="null"/> if the message doesn't name one.</returns>
    internal static string? ParseGpuName(string message) =>
        DeviceNamePattern().Match(message) is { Success: true } match ? match.Groups["name"].Value.Trim() : null;

    /// <summary>Reads the GPU backend that whisper.cpp chose from a log message.</summary>
    /// <param name="message">The log message.</param>
    /// <returns>The backend, such as <c>Vulkan0</c>, or <see langword="null"/> if the message doesn't name one.</returns>
    internal static string? ParseGpuBackend(string message) =>
        BackendPattern().Match(message) is { Success: true } match ? match.Groups["backend"].Value : null;

    private static void Observe(string message)
    {
        gpuName ??= ParseGpuName(message);
        gpuBackend = ParseGpuBackend(message) ?? gpuBackend;
    }

    // Vulkan: "ggml_vulkan: 0 = NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | ..."
    // CUDA: "  Device 0: NVIDIA GeForce RTX 3080, compute capability 8.6, VMM: yes"
    // Metal: "ggml_metal_device_init: GPU name:   Apple M1"
    [GeneratedRegex(@"(ggml_vulkan: 0 = (?<name>[^|]+?)(?: \([^)]*\))? \||Device 0: (?<name>[^,]+),|GPU name:\s+(?<name>.+))")]
    private static partial Regex DeviceNamePattern();

    // "whisper_backend_init_gpu: using Vulkan0 backend"
    [GeneratedRegex(@"whisper_backend_init_gpu: using (?<backend>\S+) backend")]
    private static partial Regex BackendPattern();
}
