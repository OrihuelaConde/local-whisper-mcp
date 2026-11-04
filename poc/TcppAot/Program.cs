using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Transcribes a WAV file through the C API of transcribe.cpp under Native AOT. Timings go to stderr.
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

if (!options.TryGetValue("model", out var model) || !options.TryGetValue("audio", out var audio))
{
    Console.Error.WriteLine("Usage: tcpp-aot --model GGUF --audio WAV [--backend auto|cpu|vulkan] [--threads N] [--repeat N] [--out TXT]");
    return 2;
}

var backend = options.GetValueOrDefault("backend", "vulkan") switch
{
    "auto" => Native.BackendAuto,
    "cpu" => Native.BackendCpu,
    "vulkan" => Native.BackendVulkan,
    var other => throw new ArgumentException($"Unknown backend '{other}'."),
};
var threads = int.Parse(options.GetValueOrDefault("threads", "8"), CultureInfo.InvariantCulture);
var repeat = int.Parse(options.GetValueOrDefault("repeat", "2"), CultureInfo.InvariantCulture);

Log($"library {Marshal.PtrToStringUTF8(Native.transcribe_version())} at commit {Marshal.PtrToStringUTF8(Native.transcribe_version_commit())}");

// The library is pre-1.0, so a binding verifies its view of each struct before it uses one.
RequireSize<Native.ModelLoadParams>(Native.AbiModelLoadParams);
RequireSize<Native.SessionParams>(Native.AbiSessionParams);

var samples = WaveReader.Read(audio);
Log($"audio: {samples.Length / 16000.0:F2} s");

var watch = Stopwatch.StartNew();
Check(Native.transcribe_init_backends_default(), "loading the backend modules");
Log($"backend modules: {watch.Elapsed.TotalSeconds:F2} s, {Native.transcribe_device_count()} devices");

watch.Restart();
Native.ModelLoadParams loadParams = default;
Native.transcribe_model_load_params_init(ref loadParams);
loadParams.Backend = backend;
Check(Native.transcribe_model_load_file(model, in loadParams, out var modelHandle), "loading the model");
Native.SessionParams sessionParams = default;
Native.transcribe_session_params_init(ref sessionParams);
sessionParams.Threads = threads;
Check(Native.transcribe_session_init(modelHandle, in sessionParams, out var session), "opening the session");
Log($"load: {watch.Elapsed.TotalSeconds:F2} s on {Marshal.PtrToStringUTF8(Native.transcribe_model_backend(modelHandle))}");

var text = string.Empty;
for (var run = 1; run <= repeat; run++)
{
    watch.Restart();
    unsafe
    {
        fixed (float* pcm = samples)
        {
            Check(Native.transcribe_run(session, pcm, samples.Length, 0), "transcribing");
        }
    }

    Log($"run {run}: {watch.Elapsed.TotalSeconds:F2} s");
    text = Marshal.PtrToStringUTF8(Native.transcribe_full_text(session))?.Trim() ?? string.Empty;
}

Native.transcribe_session_free(session);
Native.transcribe_model_free(modelHandle);

if (options.TryGetValue("out", out var outPath))
{
    File.WriteAllText(outPath, text, new UTF8Encoding(false));
}
else
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine(text);
}

using (var self = Process.GetCurrentProcess())
{
    Log($"peak working set: {self.PeakWorkingSet64 / (1024 * 1024)} MiB");
}

return 0;

static void Log(string message) => Console.Error.WriteLine($"[tcpp-aot] {message}");

static void Check(int status, string step)
{
    if (status != 0)
    {
        throw new InvalidOperationException($"{step} failed: {Marshal.PtrToStringUTF8(Native.transcribe_status_string(status))}");
    }
}

static void RequireSize<T>(int abiStruct)
    where T : unmanaged
{
    var native = (long)Native.transcribe_abi_struct_size(abiStruct);
    var managed = (long)Marshal.SizeOf<T>();
    if (native != managed)
    {
        throw new InvalidOperationException($"{typeof(T).Name} is {managed} bytes here and {native} bytes in the library.");
    }
}

/// <summary>Declares the part of the transcribe.cpp C API (include/transcribe.h) that this program uses.</summary>
internal static partial class Native
{
    /// <summary>Selects whichever backend the library ranks first.</summary>
    public const int BackendAuto = 0;

    /// <summary>Selects the CPU backend.</summary>
    public const int BackendCpu = 1;

    /// <summary>Selects the Vulkan backend.</summary>
    public const int BackendVulkan = 3;

    /// <summary>Identifies <see cref="ModelLoadParams"/> to <see cref="transcribe_abi_struct_size"/>.</summary>
    public const int AbiModelLoadParams = 0;

    /// <summary>Identifies <see cref="SessionParams"/> to <see cref="transcribe_abi_struct_size"/>.</summary>
    public const int AbiSessionParams = 1;

    private const string Library = "transcribe";

    /// <summary>Mirrors <c>struct transcribe_model_load_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ModelLoadParams
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The backend request.</summary>
        public int Backend;

        /// <summary>The exact device, or zero for the backend's choice.</summary>
        public nint Device;

        /// <summary>A nonzero value if the weights are copied instead of memory-mapped.</summary>
        public byte NoMmap;
    }

    /// <summary>Mirrors <c>struct transcribe_session_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SessionParams
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The CPU thread count, or zero for the library's default.</summary>
        public int Threads;

        /// <summary>The data type of the decoder's key-value cache.</summary>
        public int KvType;

        /// <summary>The cap on the decoder context, or zero for the model's maximum.</summary>
        public int Ctx;
    }

    [LibraryImport(Library)]
    public static partial nint transcribe_version();

    [LibraryImport(Library)]
    public static partial nint transcribe_version_commit();

    [LibraryImport(Library)]
    public static partial nint transcribe_status_string(int status);

    [LibraryImport(Library)]
    public static partial nuint transcribe_abi_struct_size(int which);

    [LibraryImport(Library)]
    public static partial int transcribe_init_backends_default();

    [LibraryImport(Library)]
    public static partial int transcribe_device_count();

    [LibraryImport(Library)]
    public static partial void transcribe_model_load_params_init(ref ModelLoadParams parameters);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int transcribe_model_load_file(string path, in ModelLoadParams parameters, out nint model);

    [LibraryImport(Library)]
    public static partial nint transcribe_model_backend(nint model);

    [LibraryImport(Library)]
    public static partial void transcribe_session_params_init(ref SessionParams parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_session_init(nint model, in SessionParams parameters, out nint session);

    /// <summary>Runs the transcription; a zero <paramref name="parameters"/> asks for the library's defaults.</summary>
    /// <param name="session">The session.</param>
    /// <param name="pcm">The 16 kHz mono samples.</param>
    /// <param name="count">The number of samples.</param>
    /// <param name="parameters">The address of the run parameters, or zero.</param>
    /// <returns>The status code.</returns>
    [LibraryImport(Library)]
    public static unsafe partial int transcribe_run(nint session, float* pcm, int count, nint parameters);

    [LibraryImport(Library)]
    public static partial nint transcribe_full_text(nint session);

    [LibraryImport(Library)]
    public static partial void transcribe_session_free(nint session);

    [LibraryImport(Library)]
    public static partial void transcribe_model_free(nint model);
}

/// <summary>Reads 16 kHz mono WAV files with 16-bit integer or 32-bit float samples.</summary>
internal static class WaveReader
{
    private const int FormatPcm = 1;
    private const int FormatFloat = 3;

    /// <summary>Reads a WAV file as float samples.</summary>
    /// <param name="path">The path to the file.</param>
    /// <returns>The samples.</returns>
    public static float[] Read(string path)
    {
        var data = File.ReadAllBytes(path);
        int format = 0, channels = 0, rate = 0, bits = 0;
        for (var position = 12; position + 8 <= data.Length;)
        {
            var id = Encoding.ASCII.GetString(data, position, 4);
            var size = BitConverter.ToInt32(data, position + 4);
            var body = position + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(data, body);
                channels = BitConverter.ToUInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToUInt16(data, body + 14);
                if (format == 0xFFFE)
                {
                    format = BitConverter.ToUInt16(data, body + 24);
                }
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != 16000)
                {
                    throw new NotSupportedException($"Expected 16 kHz mono, found {rate} Hz with {channels} channels.");
                }

                return (format, bits) switch
                {
                    (FormatPcm, 16) => MemoryMarshal.Cast<byte, short>(data.AsSpan(body, size)).ToArray().Select(sample => sample / 32768f).ToArray(),
                    (FormatFloat, 32) => MemoryMarshal.Cast<byte, float>(data.AsSpan(body, size)).ToArray(),
                    _ => throw new NotSupportedException($"Unsupported WAV format {format} with {bits} bits."),
                };
            }

            position = body + size + (size & 1);
        }

        throw new InvalidDataException("The file has no data chunk.");
    }
}
