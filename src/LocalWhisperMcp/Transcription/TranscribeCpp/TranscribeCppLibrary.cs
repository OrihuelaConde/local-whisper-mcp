using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp.TranscribeCpp;

/// <summary>Loads the bundled transcribe.cpp library once per process and checks that it's the version the server expects.</summary>
/// <remarks>
/// The library and its backend modules live in <c>runtimes/transcribe/&lt;rid&gt;/</c> next to the
/// executable. The import resolver loads the library from there by absolute path, which also
/// lets Windows find its <c>ggml</c> dependencies in the same folder, and the backend modules are
/// registered from that folder only.
/// </remarks>
internal static unsafe class TranscribeCppLibrary
{
    private static readonly Lock Gate = new();
    private static ILogger? logger;
    private static bool initialized;
    private static Exception? failure;

    /// <summary>Gets the folder that holds the bundled library for this platform.</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "runtimes", "transcribe", RuntimeIdentifier);

    /// <summary>Gets the runtime identifier of this process, such as <c>win-x64</c>.</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            return $"{platform}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Gets a value indicating whether this build includes the library for this platform.</summary>
    public static bool IsInstalled => File.Exists(LibraryPath);

    private static string LibraryPath => Path.Combine(Directory, OperatingSystem.IsWindows() ? "transcribe.dll" : OperatingSystem.IsMacOS() ? "libtranscribe.dylib" : "libtranscribe.so");

    /// <summary>Loads the library and registers its backends, the first time a model needs it.</summary>
    /// <param name="cpuOnly">Whether to register only the CPU backend, so no GPU driver code runs.</param>
    /// <param name="log">The logger that receives the library's messages.</param>
    /// <exception cref="TranscribeCppException">The library is missing, is another version, or has no usable backend.</exception>
    /// <remarks>The backends are fixed for the life of the process, as the library requires.</remarks>
    public static void EnsureInitialized(bool cpuOnly, ILogger log)
    {
        lock (Gate)
        {
            if (failure is not null)
            {
                throw new TranscribeCppException(failure.Message, failure);
            }

            if (initialized)
            {
                return;
            }

            try
            {
                Initialize(cpuOnly, log);
                initialized = true;
            }
            catch (Exception exception) when (exception is TranscribeCppException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // A library that failed to load stays unusable in this process, as Whisper.net's does.
                failure = exception is TranscribeCppException ? exception : new TranscribeCppException($"The transcribe.cpp library in {Directory} couldn't load: {exception.Message}", exception);
                throw new TranscribeCppException(failure.Message, failure);
            }
        }
    }

    /// <summary>Throws if a library call failed.</summary>
    /// <param name="status">The status code the call returned.</param>
    /// <param name="step">What the call did, for the message.</param>
    /// <exception cref="TranscribeCppException">The status isn't <see cref="Native.Ok"/>.</exception>
    public static void Check(int status, string step)
    {
        if (status != Native.Ok)
        {
            throw new TranscribeCppException($"transcribe.cpp failed {step}: {Native.Describe(status)}.", status);
        }
    }

    /// <summary>Encodes a string as a null-terminated UTF-8 buffer.</summary>
    /// <param name="value">The string.</param>
    /// <returns>The bytes, with a trailing zero.</returns>
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static void Initialize(bool cpuOnly, ILogger log)
    {
        if (!IsInstalled)
        {
            throw new TranscribeCppException($"This build of Local Whisper doesn't include transcribe.cpp for {RuntimeIdentifier}, so it can't run Parakeet models.");
        }

        NativeLibrary.SetDllImportResolver(typeof(TranscribeCppLibrary).Assembly, Resolve);

        var version = Native.ReadString(Native.transcribe_version());
        if (version != Native.SupportedVersion)
        {
            throw new TranscribeCppException($"The transcribe.cpp library in {Directory} is version {version}, but this server needs {Native.SupportedVersion}. Reinstall Local Whisper from its release.");
        }

        RequireSize<Native.BackendInitParams>(Native.AbiStruct.BackendInitParams);
        RequireSize<Native.ModelLoadParams>(Native.AbiStruct.ModelLoadParams);
        RequireSize<Native.SessionParams>(Native.AbiStruct.SessionParams);
        RequireSize<Native.RunParams>(Native.AbiStruct.RunParams);
        RequireSize<Native.Capabilities>(Native.AbiStruct.Capabilities);
        RequireSize<Native.Segment>(Native.AbiStruct.Segment);
        RequireSize<Native.Word>(Native.AbiStruct.Word);
        RequireSize<Native.Token>(Native.AbiStruct.Token);
        RequireSize<Native.DeviceInfo>(Native.AbiStruct.DeviceInfo);

        logger = log;
        Native.transcribe_log_set(&OnLog, null);

        var directory = Utf8(Directory);
        fixed (byte* path = directory)
        {
            Native.BackendInitParams parameters;
            Native.transcribe_backend_init_params_init(&parameters);
            parameters.ArtifactDirectory = path;
            parameters.AllowedBackends = cpuOnly ? Native.BackendMaskCpu : Native.BackendMaskAll;
            Check(Native.transcribe_init_backends_ex(&parameters), $"to load its backends from {Directory}");
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Native.Library ? NativeLibrary.Load(LibraryPath) : 0;

    private static void RequireSize<T>(Native.AbiStruct which)
        where T : unmanaged
    {
        var expected = (long)Native.transcribe_abi_struct_size(which);
        if (expected != sizeof(T))
        {
            throw new TranscribeCppException($"The transcribe.cpp library lays out {which} in {expected} bytes, and this server in {sizeof(T)}. Reinstall Local Whisper from its release.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* message, void* userData)
    {
        // The library logs every load and run step; only its warnings and errors matter to users.
        var logLevel = level switch
        {
            3 => LogLevel.Error,
            2 => LogLevel.Warning,
            _ => LogLevel.Debug,
        };
        // An exception can't cross back into native code: it would end the process.
        try
        {
            if (logger is { } target && target.IsEnabled(logLevel))
            {
                target.Log(logLevel, "transcribe.cpp: {Message}", Native.ReadString(message).TrimEnd());
            }
        }
        catch (Exception)
        {
            // A message that can't be logged is dropped.
        }
    }
}

/// <summary>Reports that the transcribe.cpp library couldn't load or failed a call.</summary>
internal sealed class TranscribeCppException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TranscribeCppException"/> class.</summary>
    public TranscribeCppException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TranscribeCppException"/> class with a message.</summary>
    /// <param name="message">The message.</param>
    public TranscribeCppException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TranscribeCppException"/> class with a message and the status code of the failed call.</summary>
    /// <param name="message">The message.</param>
    /// <param name="status">The library's status code.</param>
    public TranscribeCppException(string message, int status)
        : base(message) => Status = status;

    /// <summary>Gets the library's status code, or <see langword="null"/> if the failure didn't come from a call.</summary>
    public int? Status { get; }

    /// <summary>Initializes a new instance of the <see cref="TranscribeCppException"/> class with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public TranscribeCppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
