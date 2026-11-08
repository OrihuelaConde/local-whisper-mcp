using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalWhisperMcp.TranscribeCpp;
using Microsoft.Extensions.Logging;

namespace LocalWhisperMcp;

/// <summary>Holds one Parakeet model loaded in transcribe.cpp and transcribes 16 kHz mono samples with it.</summary>
/// <remarks>
/// The model cuts long audio into segments at pauses by itself, so it needs no VAD, and it takes
/// no prompt. Every run goes through <see cref="LoopGuard"/>.
/// </remarks>
internal sealed class ParakeetModel : ISpeechModel
{
    private const int SamplesPerMs = AudioDecoder.SampleRate / 1000;

    private readonly ILogger logger;
    private readonly HashSet<string> languages;
    // One byte that the abort callback reads; the cancellation token sets it.
    private nint abortFlag;
    private nint model;
    private nint session;

    /// <summary>Initializes a new instance of the <see cref="ParakeetModel"/> class and loads the model.</summary>
    /// <param name="modelPath">The path to the GGUF model file.</param>
    /// <param name="runtime">The preferred runtime: <c>auto</c>, <c>cpu</c>, <c>vulkan</c>, or <c>cuda</c>.</param>
    /// <param name="threads">The number of CPU threads to use.</param>
    /// <param name="logger">The logger for load events, loop repairs, and the library's messages.</param>
    /// <exception cref="TranscribeCppException">The library or the model couldn't load.</exception>
    public unsafe ParakeetModel(string modelPath, string runtime, int threads, ILogger logger)
    {
        ModelPath = modelPath;
        this.logger = logger;
        TranscribeCppLibrary.EnsureInitialized(cpuOnly: runtime == "cpu", logger);

        model = Load(modelPath, runtime);
        try
        {
            Native.Capabilities capabilities;
            Native.transcribe_capabilities_init(&capabilities);
            TranscribeCppLibrary.Check(Native.transcribe_model_get_capabilities(model, &capabilities), "to read the model's capabilities");
            languages = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < capabilities.LanguageCount; i++)
            {
                languages.Add(Native.ReadString(capabilities.Languages[i]));
            }

            Native.SessionParams parameters;
            Native.transcribe_session_params_init(&parameters);
            parameters.Threads = threads;
            nint created;
            TranscribeCppLibrary.Check(Native.transcribe_session_init(model, &parameters, &created), "to open a session");
            session = created;

            abortFlag = (nint)NativeMemory.AllocZeroed(1);
            Native.transcribe_set_abort_callback(session, &OnAbort, (void*)abortFlag);
            Device = DescribeDevice(Native.transcribe_model_device(model));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public string ModelPath { get; }

    /// <inheritdoc/>
    public SpeechEngine Engine => SpeechEngine.TranscribeCpp;

    /// <inheritdoc/>
    public string? Device { get; }

    /// <summary>Gets the language codes the model supports.</summary>
    public IReadOnlyCollection<string> Languages => languages;

    /// <inheritdoc/>
    public async Task<TranscriptionResult> TranscribeAsync(float[] samples, SpeechRequest request, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioDecoder.SampleRate);
        CheckLanguage(languages, request.Language, ModelCatalog.GetModelName(Path.GetFileName(ModelPath)) ?? Path.GetFileName(ModelPath));

        if (samples.Length == 0)
        {
            return new TranscriptionResult([], [], duration, null);
        }

        // The library reports no progress, so the client gets one message when the work starts.
        progress?.Report(0);
        var language = request.Language == "auto" ? null : TranscribeCppLibrary.Utf8(request.Language);
        Marshal.WriteByte(abortFlag, 0);
        using var registration = cancellationToken.Register(static state => Marshal.WriteByte((nint)state!, 1), abortFlag);
        var words = await Task.Run(
            () => LoopGuard.Transcribe(
                (startMs, endMs) => Decode(samples, startMs, endMs, language, cancellationToken),
                (long)duration.TotalMilliseconds,
                signal => logger.LogWarning("Parakeet got stuck at {Seconds:F1} s ({Reason}); transcribing that stretch again in two parts.", signal.AtMs / 1000.0, signal.Reason)),
            CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();

        // Parakeet has no language output, so the json format reports null.
        return new TranscriptionResult(SegmentBuilder.Build(words), [new SpeechSpan(TimeSpan.Zero, duration)], duration, null);
    }

    /// <summary>Checks that a model supports a language.</summary>
    /// <param name="languages">The language codes the model supports.</param>
    /// <param name="language">A language code, or <c>auto</c>, which every model accepts.</param>
    /// <param name="modelName">The model name, for the message.</param>
    /// <exception cref="ArgumentException">The model doesn't support the language; the parameter name is <c>language</c>.</exception>
    internal static void CheckLanguage(IReadOnlyCollection<string> languages, string language, string modelName)
    {
        if (language != "auto" && !languages.Contains(language))
        {
            throw new ArgumentException(
                $"{modelName} doesn't support the language '{language}'. It supports {string.Join(", ", languages.Order(StringComparer.Ordinal))}. " +
                $"Use a Whisper model, such as {ServerSettings.DefaultModelName}, for other languages.",
                "language");
        }
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (session != 0)
        {
            Native.transcribe_session_free(session);
            session = 0;
        }

        if (model != 0)
        {
            Native.transcribe_model_free(model);
            model = 0;
        }

        if (abortFlag != 0)
        {
            NativeMemory.Free((void*)abortFlag);
            abortFlag = 0;
        }
    }

    private static unsafe string DescribeDevice(nint device)
    {
        if (device == 0)
        {
            return "CPU";
        }

        Native.DeviceInfo info;
        Native.transcribe_device_info_init(&info);
        if (Native.transcribe_device_get_info(device, &info) != Native.Ok || info.DeviceType == 0)
        {
            return "CPU";
        }

        var api = Native.ReadString(info.Kind) switch
        {
            "vulkan" => "Vulkan",
            "metal" => "Metal",
            "cuda" => "CUDA",
            "rocm" => "ROCm",
            var other => other,
        };
        var name = Native.ReadString(info.Description) is { Length: > 0 } description ? description : Native.ReadString(info.Name);
        return $"{name} ({api})";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte OnAbort(void* userData) => *(byte*)userData;

    private unsafe nint Load(string modelPath, string runtime)
    {
        // Every preference ends with the CPU, as Whisper's runtimes do. AUTO picks the best GPU the
        // library registered: Vulkan here, Metal on a Mac.
        int[] requests = runtime switch
        {
            "cpu" => [Native.BackendCpu],
            "vulkan" or "cuda" => [Native.BackendVulkan, Native.BackendCpu],
            _ => [Native.BackendAuto, Native.BackendCpu],
        };
        if (runtime == "cuda")
        {
            logger.LogWarning("Parakeet models don't run on CUDA in this version; using Vulkan, or the CPU if Vulkan isn't available.");
        }

        var path = TranscribeCppLibrary.Utf8(modelPath);
        TranscribeCppException? last = null;
        foreach (var request in requests.Where(Native.transcribe_backend_available))
        {
            Native.ModelLoadParams parameters;
            Native.transcribe_model_load_params_init(&parameters);
            parameters.Backend = request;
            nint loaded;
            int status;
            fixed (byte* file = path)
            {
                status = Native.transcribe_model_load_file(file, &parameters, &loaded);
            }

            if (status == Native.Ok && loaded != 0)
            {
                return loaded;
            }

            last = new TranscribeCppException($"transcribe.cpp couldn't load {Path.GetFileName(modelPath)}: {Native.Describe(status)}.");
            logger.LogWarning("{Message} Trying the next backend.", last.Message);
        }

        throw last ?? new TranscribeCppException("transcribe.cpp has no backend that can run the model.");
    }

    private unsafe DecodeRun Decode(float[] samples, long startMs, long endMs, byte[]? language, CancellationToken cancellationToken)
    {
        var first = (int)Math.Clamp(startMs * SamplesPerMs, 0, samples.Length);
        var last = (int)Math.Clamp(endMs * SamplesPerMs, first, samples.Length);
        int status;
        fixed (float* pcm = samples)
        fixed (byte* code = language)
        {
            Native.RunParams parameters;
            Native.transcribe_run_params_init(&parameters);
            parameters.Timestamps = Native.TimestampsToken;
            parameters.Language = code;
            status = Native.transcribe_run(session, pcm + first, last - first, &parameters);
        }

        if (status == Native.ErrorAborted)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var stoppedEarly = status is Native.ErrorOutputRepetition or Native.ErrorOutputTruncated;
        if (status != Native.Ok && !stoppedEarly)
        {
            TranscribeCppLibrary.Check(status, "to transcribe");
        }

        var words = new List<TimedWord>();
        Native.Word word;
        Native.transcribe_word_init(&word);
        for (var i = 0; i < Native.transcribe_n_words(session); i++)
        {
            if (Native.transcribe_get_word(session, i, &word) == Native.Ok && word.Text != null)
            {
                words.Add(new TimedWord(Native.ReadString(word.Text), word.StartMs, word.EndMs, word.SegmentIndex));
            }
        }

        var tokens = new List<long>();
        Native.Token token;
        Native.transcribe_token_init(&token);
        for (var i = 0; i < Native.transcribe_n_tokens(session); i++)
        {
            if (Native.transcribe_get_token(session, i, &token) == Native.Ok && token.Text != null)
            {
                tokens.Add(token.StartMs);
            }
        }

        var segments = new List<TimedSpan>();
        Native.Segment segment;
        Native.transcribe_segment_init(&segment);
        for (var i = 0; i < Native.transcribe_n_segments(session); i++)
        {
            if (Native.transcribe_get_segment(session, i, &segment) == Native.Ok)
            {
                segments.Add(new TimedSpan(segment.StartMs, segment.EndMs));
            }
        }

        return new DecodeRun(words, tokens, segments, stoppedEarly);
    }
}
