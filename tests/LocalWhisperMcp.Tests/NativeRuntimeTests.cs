namespace LocalWhisperMcp.Tests;

public sealed class NativeRuntimeTests
{
    [Theory]
    [InlineData("ggml_vulkan: 0 = NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | fp16: 1 | bf16: 1 | warp size: 32", "NVIDIA GeForce RTX 3080")]
    [InlineData("ggml_vulkan: 0 = AMD Radeon RX 7800 XT (RADV NAVI32) | uma: 0 | fp16: 1", "AMD Radeon RX 7800 XT")]
    [InlineData("  Device 0: NVIDIA GeForce RTX 4090, compute capability 8.9, VMM: yes", "NVIDIA GeForce RTX 4090")]
    [InlineData("ggml_metal_device_init: GPU name:   Apple M2 Pro", "Apple M2 Pro")]
    [InlineData("whisper_init_with_params_no_state: use gpu    = 1", null)]
    public void The_gpu_name_comes_from_the_backend_log(string message, string? expected) =>
        Assert.Equal(expected, NativeRuntime.ParseGpuName(message));

    [Theory]
    [InlineData("whisper_backend_init_gpu: using Vulkan0 backend", "Vulkan0")]
    [InlineData("whisper_backend_init_gpu: device 0: Vulkan0 (type: 1)", null)]
    [InlineData("whisper_backend_init_gpu: no GPU found", null)]
    public void The_gpu_backend_comes_from_the_whisper_log(string message, string? expected) =>
        Assert.Equal(expected, NativeRuntime.ParseGpuBackend(message));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080", "Vulkan0", "NVIDIA GeForce RTX 3080 (Vulkan)")]
    [InlineData("Apple M2 Pro", "MTL0", "Apple M2 Pro (Metal)")]
    [InlineData(null, "CUDA0", "CUDA0 (CUDA)")]
    [InlineData("NVIDIA GeForce RTX 3080", null, "CPU")]
    public void The_device_names_the_gpu_and_its_api(string? name, string? backend, string expected) =>
        Assert.Equal(expected, NativeRuntime.DescribeDevice(name, backend));
}
