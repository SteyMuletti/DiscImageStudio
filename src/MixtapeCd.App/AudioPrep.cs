using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DiscImageStudio.Cd;

namespace MixtapeCd.App;

/// <summary>Turns any song file into CD audio (44.1 kHz, 16-bit, stereo WAV) using ffmpeg.</summary>
internal static class AudioPrep
{
    internal static readonly string[] Extensions =
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma"];

    private static readonly string CacheDirectory =
        Path.Combine(Path.GetTempPath(), "MixtapeCD", "songs");

    internal static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>ffmpeg next to the app (bundled build) or on PATH.</summary>
    internal static string? FindFfmpeg()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>MP3 for an MP3 disc: the file itself, or a 320 kbps conversion of other formats.</summary>
    internal static async Task<string> PrepareMp3Async(string source, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return source;
        return await ConvertAsync(source, ".mp3", ["-c:a", "libmp3lame", "-b:a", "320k", "-ar", "44100", "-f", "mp3"],
            cancellationToken);
    }

    /// <summary>Song length from ffmpeg's header read; 0 when ffmpeg isn't available.</summary>
    internal static async Task<double> ProbeSecondsAsync(string source, CancellationToken cancellationToken)
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return 0;
        ProcessStartInfo start = new(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-i", source }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string text = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text,
            @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        return m.Success
            ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
              + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    /// <summary>Returns a CD-ready WAV for <paramref name="source"/>: the file itself when it already
    /// is one, otherwise a cached ffmpeg conversion.</summary>
    internal static async Task<string> PrepareAsync(string source, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                CdMixtapeGenerator.SongSectors(source, 0);
                return source; // already 44.1 kHz/16-bit/stereo PCM
            }
            catch (InvalidDataException) { }
        }

        return await ConvertAsync(source, ".wav", ["-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav"],
            cancellationToken);
    }

    private static async Task<string> ConvertAsync(string source, string extension, string[] codecArgs,
        CancellationToken cancellationToken)
    {
        string ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "ffmpeg is needed to read this file type. Put ffmpeg.exe next to MixtapeCD.exe or install it.");
        FileInfo info = new(source);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24];
        Directory.CreateDirectory(CacheDirectory);
        string output = Path.Combine(CacheDirectory, key + extension);
        if (File.Exists(output)) return output;

        string partial = output + ".part" + extension;
        ProcessStartInfo start = new(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-y", "-i", source, "-map", "0:a:0" }
                     .Concat(codecArgs).Append(partial))
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            File.Delete(partial);
            string message = (await errors).Trim();
            throw new InvalidDataException(message.Length > 0 ? message.Split('\n')[^1] : "ffmpeg could not read this file.");
        }
        File.Move(partial, output, overwrite: true);
        return output;
    }
}
