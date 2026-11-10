using System.Runtime.InteropServices;

namespace LocalWhisperMcp.TranscribeCpp;

/// <summary>Declares the part of the transcribe.cpp C API (<c>include/transcribe.h</c>) that the server uses.</summary>
/// <remarks>
/// The structs mirror the library's layout for version <see cref="SupportedVersion"/>. C <c>bool</c>
/// fields are bytes. <see cref="TranscribeCppLibrary"/> checks each struct's size against the
/// library before the first call that passes one.
/// </remarks>
internal static unsafe partial class Native
{
    /// <summary>The library version these declarations match.</summary>
    public const string SupportedVersion = "0.4.1";

    /// <summary>The library name that the import resolver maps to the bundled file.</summary>
    public const string Library = "transcribe";

    /// <summary>The status code of a successful call.</summary>
    public const int Ok = 0;

    /// <summary>The status code when a compute backend failed, such as a GPU that stopped responding.</summary>
    public const int ErrorBackend = 8;

    /// <summary>The status code when the abort callback stopped the run.</summary>
    public const int ErrorAborted = 13;

    /// <summary>The status code when the decode hit its output budget.</summary>
    public const int ErrorOutputTruncated = 18;

    /// <summary>The status code when the decode fell into a repetition loop and was stopped.</summary>
    public const int ErrorOutputRepetition = 19;

    /// <summary>The backend request that lets the library pick the best device.</summary>
    public const int BackendAuto = 0;

    /// <summary>The backend request for the CPU.</summary>
    public const int BackendCpu = 1;

    /// <summary>The backend request for Metal.</summary>
    public const int BackendMetal = 2;

    /// <summary>The backend request for Vulkan.</summary>
    public const int BackendVulkan = 3;

    /// <summary>The allowed-backends mask bit for the CPU.</summary>
    public const uint BackendMaskCpu = 0x1;

    /// <summary>The allowed-backends mask that allows every backend.</summary>
    public const uint BackendMaskAll = 0xFFFFFFFF;

    /// <summary>The run parameter that asks for token timestamps, which also fills the word rows.</summary>
    public const int TimestampsToken = 4;

    /// <summary>The <c>transcribe_abi_struct</c> identifiers of the structs declared here.</summary>
    public enum AbiStruct
    {
        /// <summary><c>struct transcribe_model_load_params</c>.</summary>
        ModelLoadParams = 0,

        /// <summary><c>struct transcribe_session_params</c>.</summary>
        SessionParams = 1,

        /// <summary><c>struct transcribe_run_params</c>.</summary>
        RunParams = 2,

        /// <summary><c>struct transcribe_capabilities</c>.</summary>
        Capabilities = 4,

        /// <summary><c>struct transcribe_segment</c>.</summary>
        Segment = 6,

        /// <summary><c>struct transcribe_word</c>.</summary>
        Word = 7,

        /// <summary><c>struct transcribe_token</c>.</summary>
        Token = 8,

        /// <summary><c>struct transcribe_device_info</c>.</summary>
        DeviceInfo = 13,

        /// <summary><c>struct transcribe_backend_init_params</c>.</summary>
        BackendInitParams = 15,
    }

    /// <summary>Mirrors <c>struct transcribe_backend_init_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BackendInitParams
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The UTF-8 directory with the backend modules.</summary>
        public byte* ArtifactDirectory;

        /// <summary>The backends the library may register.</summary>
        public uint AllowedBackends;
    }

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
        public int Context;
    }

    /// <summary>Mirrors <c>struct transcribe_run_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RunParams
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The task: transcribe, translate, or instruct.</summary>
        public int Task;

        /// <summary>The timestamp granularity to return.</summary>
        public int Timestamps;

        /// <summary>The punctuation and capitalization mode.</summary>
        public int Pnc;

        /// <summary>The inverse text normalization mode.</summary>
        public int Itn;

        /// <summary>The diarization mode.</summary>
        public int Diarize;

        /// <summary>The UTF-8 language code, or null to detect it.</summary>
        public byte* Language;

        /// <summary>The UTF-8 translation target, or null.</summary>
        public byte* TargetLanguage;

        /// <summary>A nonzero value to keep the model's special tags in the text.</summary>
        public byte KeepSpecialTags;

        /// <summary>A family-specific extension, or null.</summary>
        public nint Family;

        /// <summary>The speculative decoding draft length, or -1 for the family default.</summary>
        public int SpecDrafts;

        /// <summary>The vocabulary terms, or null.</summary>
        public nint Vocabulary;

        /// <summary>The number of vocabulary terms.</summary>
        public int VocabularyCount;

        /// <summary>The UTF-8 context prompt, or null.</summary>
        public byte* Prompt;

        /// <summary>The UTF-8 transcript prefix, or null.</summary>
        public byte* Prefix;
    }

    /// <summary>Mirrors <c>struct transcribe_capabilities</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Capabilities
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The sample rate the model expects.</summary>
        public int NativeSampleRate;

        /// <summary>The number of language codes.</summary>
        public int LanguageCount;

        /// <summary>The UTF-8 language codes.</summary>
        public byte** Languages;

        /// <summary>The finest timestamp granularity the model produces.</summary>
        public int MaxTimestampKind;

        /// <summary>A nonzero value if the model detects the language.</summary>
        public byte SupportsLanguageDetect;

        /// <summary>A nonzero value if the model translates.</summary>
        public byte SupportsTranslate;

        /// <summary>A nonzero value if the model streams.</summary>
        public byte SupportsStreaming;

        /// <summary>A nonzero value if the model supports speculative decoding.</summary>
        public byte SupportsSpecDecode;

        /// <summary>The longest input, in milliseconds, or zero for no limit.</summary>
        public long MaxAudioMs;

        /// <summary>The number of translation targets.</summary>
        public int TranslateTargetCount;

        /// <summary>The UTF-8 translation targets.</summary>
        public byte** TranslateTargets;
    }

    /// <summary>Mirrors <c>struct transcribe_segment</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Segment
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The start, in milliseconds.</summary>
        public long StartMs;

        /// <summary>The end, in milliseconds.</summary>
        public long EndMs;

        /// <summary>The index of the first word.</summary>
        public int FirstWord;

        /// <summary>The number of words.</summary>
        public int WordCount;

        /// <summary>The index of the first token.</summary>
        public int FirstToken;

        /// <summary>The number of tokens.</summary>
        public int TokenCount;

        /// <summary>The UTF-8 text, owned by the session.</summary>
        public byte* Text;

        /// <summary>The 1-based speaker, or zero.</summary>
        public int SpeakerId;
    }

    /// <summary>Mirrors <c>struct transcribe_word</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Word
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The start, in milliseconds.</summary>
        public long StartMs;

        /// <summary>The end, in milliseconds.</summary>
        public long EndMs;

        /// <summary>The index of the word's segment.</summary>
        public int SegmentIndex;

        /// <summary>The index of the first token.</summary>
        public int FirstToken;

        /// <summary>The number of tokens.</summary>
        public int TokenCount;

        /// <summary>The UTF-8 text, owned by the session.</summary>
        public byte* Text;
    }

    /// <summary>Mirrors <c>struct transcribe_token</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Token
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The vocabulary id.</summary>
        public int Id;

        /// <summary>The confidence hint.</summary>
        public float Probability;

        /// <summary>The start, in milliseconds.</summary>
        public long StartMs;

        /// <summary>The end, in milliseconds.</summary>
        public long EndMs;

        /// <summary>The index of the token's segment.</summary>
        public int SegmentIndex;

        /// <summary>The index of the token's word.</summary>
        public int WordIndex;

        /// <summary>The UTF-8 text, owned by the session.</summary>
        public byte* Text;
    }

    /// <summary>Mirrors <c>struct transcribe_device_info</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        /// <summary>The struct size that the init function stamps.</summary>
        public ulong StructSize;

        /// <summary>The ggml device name, such as <c>Vulkan0</c>.</summary>
        public byte* Name;

        /// <summary>The readable description, such as the GPU model.</summary>
        public byte* Description;

        /// <summary>The vendor kind, such as <c>vulkan</c> or <c>cpu</c>.</summary>
        public byte* Kind;

        /// <summary>The stable hardware id, or null.</summary>
        public byte* DeviceId;

        /// <summary>The capacity in bytes, or zero.</summary>
        public ulong MemoryTotal;

        /// <summary>The free bytes when the struct was filled, or zero.</summary>
        public ulong MemoryFree;

        /// <summary>The CPU, GPU, integrated GPU, or accelerator axis.</summary>
        public int DeviceType;
    }

    [LibraryImport(Library)]
    public static partial byte* transcribe_version();

    [LibraryImport(Library)]
    public static partial byte* transcribe_status_string(int status);

    [LibraryImport(Library)]
    public static partial nuint transcribe_abi_struct_size(AbiStruct which);

    [LibraryImport(Library)]
    public static partial void transcribe_log_set(delegate* unmanaged[Cdecl]<int, byte*, void*, void> callback, void* userData);

    [LibraryImport(Library)]
    public static partial void transcribe_backend_init_params_init(BackendInitParams* parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_init_backends_ex(BackendInitParams* parameters);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool transcribe_backend_available(int backend);

    [LibraryImport(Library)]
    public static partial void transcribe_model_load_params_init(ModelLoadParams* parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_model_load_file(byte* path, ModelLoadParams* parameters, nint* model);

    [LibraryImport(Library)]
    public static partial void transcribe_model_free(nint model);

    [LibraryImport(Library)]
    public static partial byte* transcribe_model_backend(nint model);

    [LibraryImport(Library)]
    public static partial nint transcribe_model_device(nint model);

    [LibraryImport(Library)]
    public static partial void transcribe_device_info_init(DeviceInfo* info);

    [LibraryImport(Library)]
    public static partial int transcribe_device_get_info(nint device, DeviceInfo* info);

    [LibraryImport(Library)]
    public static partial void transcribe_capabilities_init(Capabilities* capabilities);

    [LibraryImport(Library)]
    public static partial int transcribe_model_get_capabilities(nint model, Capabilities* capabilities);

    [LibraryImport(Library)]
    public static partial void transcribe_session_params_init(SessionParams* parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_session_init(nint model, SessionParams* parameters, nint* session);

    [LibraryImport(Library)]
    public static partial void transcribe_session_free(nint session);

    [LibraryImport(Library)]
    public static partial void transcribe_set_abort_callback(nint session, delegate* unmanaged[Cdecl]<void*, byte> callback, void* userData);

    [LibraryImport(Library)]
    public static partial void transcribe_run_params_init(RunParams* parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_run(nint session, float* pcm, int sampleCount, RunParams* parameters);

    [LibraryImport(Library)]
    public static partial int transcribe_n_segments(nint session);

    [LibraryImport(Library)]
    public static partial int transcribe_n_words(nint session);

    [LibraryImport(Library)]
    public static partial int transcribe_n_tokens(nint session);

    [LibraryImport(Library)]
    public static partial void transcribe_segment_init(Segment* segment);

    [LibraryImport(Library)]
    public static partial void transcribe_word_init(Word* word);

    [LibraryImport(Library)]
    public static partial void transcribe_token_init(Token* token);

    [LibraryImport(Library)]
    public static partial int transcribe_get_segment(nint session, int index, Segment* segment);

    [LibraryImport(Library)]
    public static partial int transcribe_get_word(nint session, int index, Word* word);

    [LibraryImport(Library)]
    public static partial int transcribe_get_token(nint session, int index, Token* token);

    /// <summary>Reads a UTF-8 string that the library owns.</summary>
    /// <param name="text">The pointer, or null.</param>
    /// <returns>The string, or an empty string for null.</returns>
    public static string ReadString(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    /// <summary>Describes a status code.</summary>
    /// <param name="status">The status code.</param>
    /// <returns>The library's description of it.</returns>
    public static string Describe(int status) => $"{ReadString(transcribe_status_string(status))} (status {status})";
}
