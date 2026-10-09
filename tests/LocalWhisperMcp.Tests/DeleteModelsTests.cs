using Microsoft.Extensions.Logging.Abstractions;

namespace LocalWhisperMcp.Tests;

public sealed class DeleteModelsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("lwm-delete-").FullName;

    [Fact]
    public async Task Without_names_every_model_file_is_deleted()
    {
        CreateModel("ggml-base.bin", 2_000_000);
        CreateModel(ModelCatalog.VadModelFileName, 1_000_000);
        File.WriteAllBytes(Path.Combine(directory, "notes.txt"), [1]);
        using var host = CreateHost(out var store);

        var report = await TranscriptionTools.DeleteModelsAsync(host);

        Assert.StartsWith("Deleted 2 models and freed 3 MB:", report);
        Assert.Contains("- base (2 MB)", report);
        Assert.Contains("- the voice activity detection model (1 MB)", report);
        Assert.Equal(["notes.txt"], Directory.GetFiles(directory).Select(Path.GetFileName));
        Assert.Empty(store.GetInstalledModels());
    }

    [Fact]
    public async Task Named_models_are_deleted_and_missing_ones_are_reported()
    {
        CreateModel("ggml-base.bin", 1_000_000);
        CreateModel("ggml-tiny.bin", 1_000_000);
        using var host = CreateHost(out var store);

        var report = await TranscriptionTools.DeleteModelsAsync(host, ["ggml-tiny.bin", "small"]);

        Assert.Contains("- tiny (1 MB)", report);
        Assert.Contains("Kept small: it isn't installed.", report);
        Assert.Equal(["base"], store.GetInstalledModels());
    }

    [Fact]
    public async Task An_empty_models_folder_says_so()
    {
        using var host = CreateHost(out _);

        Assert.Equal("There are no downloaded models to delete.", await TranscriptionTools.DeleteModelsAsync(host));
    }

    [Fact]
    public void Status_lists_the_models_with_their_sizes()
    {
        CreateModel("ggml-base.bin", 147_951_465);
        CreateModel(ModelCatalog.VadModelFileName, 885_098);
        using var host = CreateHost(out _);

        Assert.Equal([new InstalledModel("base", 148)], host.GetStatus().InstalledModels);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void CreateModel(string fileName, int bytes) => File.WriteAllBytes(Path.Combine(directory, fileName), new byte[bytes]);

    private WhisperHost CreateHost(out ModelStore store)
    {
        store = new ModelStore(directory, NullLogger.Instance);
        var settings = ServerSettings.FromVariables(_ => null, Path.GetTempPath(), []) with { ModelsDirectory = directory };
        return new WhisperHost(settings, store, NullLogger<WhisperHost>.Instance);
    }
}
