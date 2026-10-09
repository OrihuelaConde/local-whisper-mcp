using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalWhisperMcp;

/// <summary>Decodes audio with ffmpeg, when it's installed.</summary>
internal static class Ffmpeg
{
    /// <summary>Checks whether an ffmpeg executable can be found.</summary>
    /// <returns><see langword="true"/> if ffmpeg was found.</returns>
    public static bool IsAvailable() => Find() is not null;

    /// <summary>Finds the ffmpeg executable.</summary>
    /// <returns>The full path of ffmpeg, or <see langword="null"/> if it isn't installed where the server looks.</returns>
    /// <remarks>
    /// The server looks at <c>LOCAL_WHISPER_FFMPEG</c> first, then the <c>PATH</c>, then the folders
    /// where package managers install ffmpeg. Apps started from the macOS Dock or Finder don't get
    /// the shell's <c>PATH</c>, so a Homebrew ffmpeg is invisible to Claude Desktop's servers unless
    /// the server looks in Homebrew's folders itself.
    /// </remarks>
    public static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("LOCAL_WHISPER_FFMPEG")?.Trim();
        if (!string.IsNullOrEmpty(configured) && !configured.StartsWith("${", StringComparison.Ordinal) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => directory.Trim('"'))
            .Concat(WellKnownDirectories());
        return directories
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> WellKnownDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            // winget, Scoop, and Chocolatey.
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        }
        else
        {
            // Homebrew on Apple Silicon and Intel, MacPorts, and the usual Unix folders.
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";
            yield return "/usr/bin";
            yield return "/snap/bin";
        }
    }

    /// <summary>Decodes an audio file to mono samples.</summary>
    /// <param name="path">The path to the audio file.</param>
    /// <param name="sampleRate">The output sample rate, in hertz.</param>
    /// <param name="cancellationToken">The token to cancel the decoding, which stops ffmpeg.</param>
    /// <returns>The samples, normalized to the range from -1 to 1.</returns>
    /// <exception cref="AudioDecodingException">ffmpeg isn't installed or can't decode the file.</exception>
    public static async Task<float[]> DecodeAsync(string path, int sampleRate, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Find() ?? "ffmpeg")
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
