using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalWhisperMcp;

/// <summary>Decodes audio with ffmpeg, when it's on the <c>PATH</c>.</summary>
internal static class Ffmpeg
{
    /// <summary>Checks whether an ffmpeg executable is on the <c>PATH</c>.</summary>
    /// <returns><see langword="true"/> if ffmpeg was found.</returns>
    public static bool IsAvailable()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim('"'), name)));
    }

    /// <summary>Decodes an audio file to mono samples.</summary>
    /// <param name="path">The path to the audio file.</param>
    /// <param name="sampleRate">The output sample rate, in hertz.</param>
    /// <param name="cancellationToken">The token to cancel the decoding, which stops ffmpeg.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    /// <exception cref="AudioDecodingException">ffmpeg isn't installed or can't decode the file.</exception>
    public static async Task<float[]> DecodeAsync(string path, int sampleRate, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-nostdin", "-v", "error", "-i", path, "-vn", "-f", "f32le", "-ac", "1", "-ar", $"{sampleRate}", "-"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new Win32Exception();
        }
        catch (Win32Exception)
        {
            throw new AudioDecodingException(
                $"Decoding {Path.GetFileName(path)} requires ffmpeg on the PATH. Install ffmpeg, or convert the file to WAV, Ogg Opus, or MP3.");
        }

        using (process)
        using (cancellationToken.Register(() => TryKill(process)))
        {
            using var pcm = new MemoryStream();
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new AudioDecodingException($"ffmpeg couldn't decode {Path.GetFileName(path)}: {(await errors).Trim()}");
            }

            return MemoryMarshal.Cast<byte, float>(pcm.GetBuffer().AsSpan(0, (int)pcm.Length)).ToArray();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}
