namespace LocalWhisperMcp.Tests;

public sealed class ModelCatalogTests
{
    [Theory]
    [InlineData("base", true)]
    [InlineData("large-v3-turbo-q8_0", true)]
    [InlineData("small.en-tdrz", true)]
    [InlineData("..", false)]
    [InlineData("../base", false)]
    [InlineData("models/base", false)]
    [InlineData(@"models\base", false)]
    [InlineData("", false)]
    public void Names_that_could_leave_the_models_directory_are_invalid(string name, bool valid) =>
        Assert.Equal(valid, ModelCatalog.IsValidName(name));

    [Fact]
    public void File_names_round_trip_to_model_names()
    {
        Assert.Equal("ggml-large-v3-turbo-q8_0.bin", ModelCatalog.GetFileName("large-v3-turbo-q8_0"));
        Assert.Equal("large-v3-turbo-q8_0", ModelCatalog.GetModelName("ggml-large-v3-turbo-q8_0.bin"));
        Assert.Null(ModelCatalog.GetModelName(ModelCatalog.VadModelFileName));
        Assert.Null(ModelCatalog.GetModelName("notes.bin"));
    }

    [Fact]
    public void Known_models_and_the_vad_model_have_download_addresses()
    {
        Assert.Equal(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            ModelCatalog.GetDownloadUri("ggml-base.bin")?.AbsoluteUri);
        Assert.Equal(
            "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin",
            ModelCatalog.GetDownloadUri(ModelCatalog.VadModelFileName)?.AbsoluteUri);
        Assert.Null(ModelCatalog.GetDownloadUri("ggml-my-finetune.bin"));
    }

    [Theory]
    [InlineData("base", "base")]
    [InlineData(" ggml-base.bin ", "base")]
    [InlineData("GGML-large-v3-turbo-q8_0.BIN", "large-v3-turbo-q8_0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Model_names_accept_the_file_name_form(string? input, string? expected) =>
        Assert.Equal(expected, ModelCatalog.Normalize(input));
}
