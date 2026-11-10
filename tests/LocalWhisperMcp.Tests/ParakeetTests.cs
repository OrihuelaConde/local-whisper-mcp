using LocalWhisperMcp.TranscribeCpp;

namespace LocalWhisperMcp.Tests;

public sealed class ParakeetTests
{
    [Fact]
    public void The_catalog_maps_parakeet_ultra_to_transcribe_cpp_and_its_gguf_file()
    {
        Assert.Equal(SpeechEngine.TranscribeCpp, ModelCatalog.GetEngine("parakeet-ultra"));
        Assert.Equal("parakeet-ultra-Q8_0.gguf", ModelCatalog.GetFileName("parakeet-ultra"));
        Assert.Equal("parakeet-ultra", ModelCatalog.GetModelName("parakeet-ultra-Q8_0.gguf"));
        Assert.Equal(
            "https://huggingface.co/handy-computer/parakeet-ultra-gguf/resolve/a1539be1b849ea7463b88343e5277b373d0c4ea9/parakeet-ultra-Q8_0.gguf",
            ModelCatalog.GetDownloadUri("parakeet-ultra-Q8_0.gguf")?.AbsoluteUri);
        Assert.Contains("parakeet-ultra", ModelCatalog.KnownModels);
        Assert.Equal("007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59", ModelCatalog.GetPinnedSha256("parakeet-ultra-Q8_0.gguf"));
        Assert.Null(ModelCatalog.GetPinnedSha256("ggml-base.bin"));
    }

    [Theory]
    [InlineData("parakeet-ultra", "parakeet-ultra")]
    [InlineData("Parakeet-Ultra", "parakeet-ultra")]
    [InlineData(" parakeet-ultra-Q8_0.gguf ", "parakeet-ultra")]
    public void Parakeet_names_accept_the_file_name_form(string input, string expected) =>
        Assert.Equal(expected, ModelCatalog.Normalize(input));

    [Fact]
    public void Whisper_models_keep_their_engine_and_unknown_gguf_files_are_not_models()
    {
        Assert.Equal(SpeechEngine.Whisper, ModelCatalog.GetEngine("large-v3-turbo-q8_0"));
        Assert.Equal(SpeechEngine.Whisper, ModelCatalog.GetEngine("my-finetune"));
        Assert.Equal("ggml-my-finetune.bin", ModelCatalog.GetFileName("my-finetune"));
        Assert.Null(ModelCatalog.GetModelName("parakeet-ultra-F16.gguf"));
        Assert.Null(ModelCatalog.GetDownloadUri("parakeet-ultra-F16.gguf"));
    }

    [Fact]
    public void The_default_model_can_be_parakeet()
    {
        var settings = ServerSettings.FromVariables(name => name == "LOCAL_WHISPER_MODEL" ? "parakeet-ultra" : null, Path.GetTempPath(), []);

        Assert.Equal("parakeet-ultra", settings.DefaultModel);
        Assert.Equal(SpeechEngine.TranscribeCpp, ModelCatalog.GetEngine(settings.DefaultModel));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("auto")]
    public void Supported_languages_and_auto_pass(string language) =>
        ParakeetModel.CheckLanguage(["en", "es"], language, "parakeet-ultra");

    [Fact]
    public void An_unsupported_language_names_the_supported_ones_and_suggests_whisper()
    {
        var exception = Assert.Throws<ArgumentException>(() => ParakeetModel.CheckLanguage(["es", "en"], "ja", "parakeet-ultra"));

        Assert.Equal("language", exception.ParamName);
        Assert.StartsWith("parakeet-ultra doesn't support the language 'ja'. It supports en, es.", exception.Message);
        Assert.Contains(ServerSettings.DefaultModelName, exception.Message);
    }

    [Fact]
    public void Hints_reach_whisper_and_are_ignored_by_parakeet()
    {
        Assert.Equal("Orihuela.", TranscriptionTools.PromptFor(SpeechEngine.Whisper, "Orihuela."));
        Assert.Null(TranscriptionTools.PromptFor(SpeechEngine.TranscribeCpp, "Orihuela."));
    }

    [Fact]
    public void Segments_end_at_sentence_ends()
    {
        var segments = SegmentBuilder.Build(
        [
            Word("Hola,", 0, 500), Word("¿cómo", 500, 900), Word("andás?", 900, 1400),
            Word("Bien.", 1600, 2000), Word("Gracias", 2100, 2500), Word("(nada).", 2500, 3000),
        ]);

        Assert.Equal(["Hola, ¿cómo andás?", "Bien.", "Gracias (nada)."], segments.Select(segment => segment.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), segments[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), segments[1].End);
    }

    [Fact]
    public void Segments_never_pass_fifteen_seconds_and_end_where_the_model_cut()
    {
        var words = Enumerable.Range(0, 40).Select(i => Word($"palabra{i}", i * 1000, (i * 1000) + 800)).ToList();
        words.Add(Word("otra", 41_000, 41_500, group: 1));

        var segments = SegmentBuilder.Build(words);

        Assert.All(segments, segment => Assert.True(segment.End - segment.Start <= SegmentBuilder.MaxDuration));
        Assert.Equal("otra", segments[^1].Text);
        Assert.Equal(TimeSpan.FromSeconds(41), segments[^1].Start);
        Assert.Equal(41, segments.Sum(segment => segment.Text.Split(' ').Length));
    }

    [Fact]
    public void Normal_speech_has_no_loop()
    {
        var words = Enumerable.Range(0, 200).Select(i => Word($"w{i % 37}", i * 300, (i * 300) + 250)).ToList();
        var tokens = Enumerable.Range(0, 400).Select(i => (long)i * 150).ToList();

        Assert.Null(LoopGuard.Detect(new DecodeRun(words, tokens, [new TimedSpan(0, 60_000)], false)));
    }

    [Fact]
    public void Sixty_tokens_in_under_two_seconds_is_a_loop()
    {
        List<long> tokens = [.. Enumerable.Range(0, 50).Select(i => (long)i * 200), .. Enumerable.Repeat(25_680L, 100)];

        var signal = LoopGuard.Detect(new DecodeRun([], tokens, [new TimedSpan(960, 25_760)], false));

        Assert.Equal(25_680, signal?.AtMs);
    }

    [Fact]
    public void A_phrase_repeated_eight_times_in_a_row_is_a_loop()
    {
        var words = new List<TimedWord> { Word("Si", 0, 200), Word("bien", 200, 400) };
        for (var i = 0; i < 8; i++)
        {
            words.AddRange([Word("de", 1000 + (i * 3000), 1500), Word("la", 2000, 2500), Word("Universidad,", 2500, 3000)]);
        }

        Assert.Equal(1000, LoopGuard.Detect(new DecodeRun(words, [], [], false))?.AtMs);
        Assert.Null(LoopGuard.Detect(new DecodeRun(words[..^3], [], [], false)));
    }

    [Fact]
    public void The_token_rate_decides_where_to_cut_when_a_repeated_phrase_also_fires()
    {
        // As in the 0 dB recording: the phrase starts at 9,800 ms and the tokens pile up from 9,840 ms.
        var words = new List<TimedWord>();
        for (var i = 0; i < 10; i++)
        {
            words.AddRange([Word("de", 9_800, 9_840), Word("la", 9_840, 9_840), Word("Universidad,", 9_840, 9_840)]);
        }

        List<long> tokens = [.. Enumerable.Range(0, 40).Select(i => (long)i * 200), .. Enumerable.Repeat(9_840L, 100)];

        var signal = LoopGuard.Detect(new DecodeRun(words, tokens, [new TimedSpan(2_400, 11_920)], false));

        Assert.Equal(9_840, signal?.AtMs);
        Assert.StartsWith("60 tokens", signal?.Reason);
    }

    [Fact]
    public void A_run_the_library_stopped_early_is_a_loop_at_its_last_token()
    {
        Assert.Equal(4_000, LoopGuard.Detect(new DecodeRun([Word("hola", 0, 500)], [0, 2_000, 4_000], [new TimedSpan(0, 5_000)], true))?.AtMs);
    }

    [Fact]
    public void A_stuck_segment_is_transcribed_again_in_two_parts_and_the_rest_is_kept()
    {
        // The first decode of the whole audio gets stuck at 10 s in the second of three segments.
        var calls = new List<(long Start, long End)>();
        DecodeRun Decode(long start, long end)
        {
            calls.Add((start, end));
            if (start == 0 && end == 30_000)
            {
                return new DecodeRun(
                    [Word("uno.", 1_000, 2_000, group: 0), Word("dos", 6_000, 7_000, group: 1), Word("Universidad", 10_000, 10_000, group: 1), Word("cuatro.", 21_000, 22_000, group: 2)],
                    [1_000, 6_000, .. Enumerable.Repeat(10_000L, 80), 21_000],
                    [new TimedSpan(500, 3_000), new TimedSpan(5_000, 10_000), new TimedSpan(20_000, 23_000)],
                    false);
            }

            // A repair decode returns times from its own start.
            return new DecodeRun([Word($"parte{calls.Count}", 100, 600)], [100], [new TimedSpan(0, end - start)], false);
        }

        var repairs = new List<LoopSignal>();
        var words = LoopGuard.Transcribe(Decode, 30_000, (signal, repaired) => repairs.Add(signal));

        Assert.Equal([(0L, 30_000L), (4_700L, 10_000L), (10_000L, 20_000L)], calls);
        Assert.Single(repairs);
        Assert.Equal(["uno.", "parte2", "parte3", "cuatro."], words.Select(word => word.Text));
        Assert.Equal([1_000L, 4_800L, 10_100L, 21_000L], words.Select(word => word.StartMs));
        Assert.Equal(4, words.Select(word => word.Group).Distinct().Count());
    }

    [Fact]
    public void A_stretch_whose_cut_would_be_the_same_range_is_never_decoded_again()
    {
        // The loop starts at the very start of the only segment, so a repair would decode the same audio.
        var calls = new List<(long, long)>();
        var outcomes = new List<bool>();
        var words = LoopGuard.Transcribe(
            (start, end) =>
            {
                calls.Add((start, end));
                return new DecodeRun([Word("x", 0, 10)], [.. Enumerable.Repeat(0L, 80)], [new TimedSpan(0, end - start)], false);
            },
            8_000,
            (signal, repaired) => outcomes.Add(repaired));

        Assert.Equal([(0L, 8_000L)], calls);
        Assert.Equal([false], outcomes);
        Assert.Empty(words);
    }

    [Fact]
    public void At_the_depth_limit_the_words_before_the_loop_are_kept_and_the_rest_dropped()
    {
        // Every decode loops halfway through its stretch, so the guard nests until it gives up.
        var outcomes = new List<bool>();
        var words = LoopGuard.Transcribe(
            (start, end) =>
            {
                var middle = (end - start) / 2;
                return new DecodeRun(
                    [Word("antes", 0, 100), Word("bucle", middle, middle)],
                    [.. Enumerable.Repeat(middle, 80)],
                    [new TimedSpan(0, end - start)],
                    false);
            },
            16_000,
            (signal, repaired) => outcomes.Add(repaired));

        Assert.Contains(true, outcomes);
        Assert.Contains(false, outcomes);
        Assert.DoesNotContain(words, word => word.Text == "bucle");
        Assert.Contains(words, word => word.Text == "antes");
    }

    [Fact]
    public void The_segment_with_the_pile_up_is_repaired_even_when_the_window_starts_in_the_previous_one()
    {
        // Normal speech, a token every 100 ms, ends a segment at 9.5 s; the next segment starts at
        // 10 s and gets stuck at 10.5 s. The 60-token window starts in the previous segment.
        List<long> tokens = [.. Enumerable.Range(0, 96).Select(i => (long)i * 100), .. Enumerable.Repeat(10_500L, 100)];
        var calls = new List<(long Start, long End)>();
        DecodeRun Decode(long start, long end)
        {
            calls.Add((start, end));
            if (start == 0)
            {
                return new DecodeRun(
                    [Word("bien.", 9_000, 9_500, group: 0), Word("Universidad", 10_500, 10_500, group: 1)],
                    tokens,
                    [new TimedSpan(0, 9_500), new TimedSpan(10_000, 20_000)],
                    false);
            }

            return new DecodeRun([Word($"parte{calls.Count}", 0, 100)], [0], [new TimedSpan(0, end - start)], false);
        }

        var signals = new List<LoopSignal>();
        var words = LoopGuard.Transcribe(Decode, 20_000, (signal, repaired) => signals.Add(signal));

        Assert.Equal(10_500, Assert.Single(signals).PileUpMs);

        // The cut clamps to the stuck segment's start, so the segment is decoded again whole, with its lead.
        Assert.Equal(["bien.", "parte2"], words.Select(word => word.Text));
        Assert.Equal((9_700L, 20_000L), calls[1]);

        // The repair stays inside the stuck segment: it starts after the previous one ends.
        Assert.All(calls.Skip(1), call => Assert.True(call.Start >= 9_500, $"A repair started at {call.Start} ms."));
    }

    [Fact]
    public void A_phrase_said_over_and_over_at_a_normal_pace_keeps_its_words()
    {
        // A chant: the phrase repeats ten times, but the tokens come at a normal pace.
        var words = new List<TimedWord>();
        for (var i = 0; i < 10; i++)
        {
            words.AddRange([Word("vamos", i * 1_500, (i * 1_500) + 400), Word("a", (i * 1_500) + 400, (i * 1_500) + 600), Word("ganar,", (i * 1_500) + 600, (i * 1_500) + 1_200)]);
        }

        words.Add(Word("fin.", 16_000, 16_500));
        List<long> tokens = [.. words.Select(word => word.StartMs)];
        var outcomes = new List<bool>();

        var result = LoopGuard.Transcribe(
            (start, end) => new DecodeRun([.. words.Select(word => word with { StartMs = word.StartMs - start, EndMs = word.EndMs - start })], [.. tokens.Select(token => token - start)], [new TimedSpan(0, end - start)], false),
            17_000,
            (signal, repaired) => outcomes.Add(repaired));

        Assert.Equal([false], outcomes);
        Assert.Equal(words.Count, result.Count);
        Assert.Equal("fin.", result[^1].Text);
    }

    [Fact]
    public void A_second_loop_in_a_later_segment_is_repaired_too()
    {
        var calls = new List<(long Start, long End)>();
        DecodeRun Decode(long start, long end)
        {
            calls.Add((start, end));
            if (start == 0)
            {
                return new DecodeRun(
                    [Word("uno.", 1_000, 2_000, group: 0), Word("bucle", 6_000, 6_000, group: 1), Word("tres.", 12_000, 13_000, group: 2), Word("otro", 17_000, 17_000, group: 3)],
                    [1_000, .. Enumerable.Repeat(6_000L, 80), 12_000, .. Enumerable.Repeat(17_000L, 80)],
                    [new TimedSpan(500, 3_000), new TimedSpan(5_000, 8_000), new TimedSpan(11_000, 14_000), new TimedSpan(16_000, 19_000)],
                    false);
            }

            return new DecodeRun([Word($"parte{calls.Count}", 0, 100)], [0], [new TimedSpan(0, end - start)], false);
        }

        var signals = new List<LoopSignal>();
        var words = LoopGuard.Transcribe(Decode, 20_000, (signal, repaired) => signals.Add(signal));

        Assert.Equal([6_000L, 17_000L], signals.Select(signal => signal.PileUpMs));
        Assert.Equal(["uno.", "parte2", "parte3", "tres.", "parte4", "parte5"], words.Select(word => word.Text));
        Assert.DoesNotContain(words, word => word.Text is "bucle" or "otro");
    }

    private static TimedWord Word(string text, long start, long end, int group = 0) => new(text, start, end, group);
}
