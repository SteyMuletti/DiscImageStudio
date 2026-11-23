using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Cd;

namespace MixtapeCd.App;

/// <summary>What ffmpeg reports about a song's first audio stream.</summary>
internal sealed record AudioInfo(string Codec, int SampleRate, int Channels, int BitrateKbps, double Seconds)
{
    /// <summary>Song tags found in the file (empty when missing).</summary>
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";

    private static readonly string[] LosslessCodecs =
        ["flac", "alac", "wavpack", "ape", "tta", "mlp", "truehd", "shorten", "als", "tak"];

    public bool IsMp3 => Codec == "mp3";
    public bool IsLossless => Codec.StartsWith("pcm_", StringComparison.Ordinal)
        || Codec.StartsWith("dsd_", StringComparison.Ordinal) || LosslessCodecs.Contains(Codec);

    public string FormatName => Codec switch
    {
        _ when Codec.StartsWith("pcm_", StringComparison.Ordinal) => "WAV/AIFF",
        _ when Codec.StartsWith("dsd_", StringComparison.Ordinal) => "DSD",
        "aac" => "AAC",
        "alac" => "ALAC",
        "vorbis" => "OGG",
        "wmav1" or "wmav2" or "wmapro" => "WMA",
        _ => Codec.ToUpperInvariant(),
    };
}

/// <summary>MP3 quality choices for non-MP3 songs. MP3s are always kept as they are.</summary>
internal enum Mp3Quality { Best320, VbrV0, Cbr192 }

/// <summary>Song conversion with ffmpeg: CD audio for audio CDs, MP3 for MP3 discs.</summary>
internal static class AudioPrep
{
    /// <summary>Anything ffmpeg can usually pull audio from, including video files.</summary>
    internal static readonly string[] Extensions =
    [
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".alac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc",
        ".wma", ".ape", ".wv", ".tta", ".mka", ".webm", ".mp2", ".mpc", ".ac3", ".eac3", ".dts", ".amr", ".caf",
        ".dsf", ".dff", ".au", ".snd", ".3gp", ".m4b", ".mp4", ".mov", ".mkv", ".avi", ".flv", ".wmv",
    ];

    /// <summary>Sample rates an MPEG-1 Layer III file can carry without resampling.</summary>
    private static readonly int[] Mp3SampleRates = [32000, 44100, 48000];

    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "MixtapeCD", "songs");

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

    private static string RequireFfmpeg() => FindFfmpeg() ?? throw new InvalidOperationException(
        "ffmpeg is needed to read this file. Put ffmpeg.exe next to MixtapeCD.exe or install it.");

    /// <summary>Reads codec, rate, channels, bitrate and length; null when ffmpeg is missing.</summary>
    internal static async Task<AudioInfo?> ProbeAsync(string source, CancellationToken cancellationToken)
    {
        if (FindFfmpeg() is null) return null;
        (_, string text) = await RunAsync(["-hide_banner", "-nostdin", "-i", source], cancellationToken);
        Match audio = Regex.Match(text, @"Stream #[^\n]*?Audio: ([a-z0-9_]+)[^\n]*");
        if (!audio.Success) throw new InvalidDataException("No audio found in this file.");
        string line = audio.Value;
        int rate = Regex.Match(line, @"(\d+) Hz") is { Success: true } r ? int.Parse(r.Groups[1].Value) : 44100;
        int channels = line.Contains(", mono", StringComparison.Ordinal) ? 1
            : Regex.Match(line, @", (\d+) channels") is { Success: true } c ? int.Parse(c.Groups[1].Value)
            : Regex.Match(line, @", (\d)\.(\d)") is { Success: true } layout
                ? int.Parse(layout.Groups[1].Value) + int.Parse(layout.Groups[2].Value)
            : 2;
        int kbps = Regex.Match(line, @"(\d+) kb/s") is { Success: true } b ? int.Parse(b.Groups[1].Value) : 0;
        double seconds = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)") is { Success: true } d
            ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
              + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        // First "title"/"artist" lines belong to the file's own metadata block (before any stream).
        string header = text[..Math.Max(0, text.IndexOf("Stream #", StringComparison.Ordinal))];
        static string Tag(string block, string name)
            => Regex.Match(block, @"^\s+" + name + @"\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        return new AudioInfo(audio.Groups[1].Value, rate, channels, kbps, seconds)
        {
            Title = Tag(header, "title"),
            Artist = Tag(header, "artist"),
        };
    }

    /// <summary>
    /// The MP3 that goes on the disc with the least quality loss:
    /// an MP3 is used unchanged (re-encoding would only lose quality);
    /// anything else is encoded once with LAME at its highest-quality settings.
    /// </summary>
    internal static async Task<string> PrepareMp3Async(string source, AudioInfo? info, Mp3Quality quality,
        CancellationToken cancellationToken)
    {
        if (info?.IsMp3 == true
            || (info is null && Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase)))
            return source;

        List<string> args = ["-c:a", "libmp3lame", "-compression_level", "0"]; // LAME -q 0: best algorithm
        args.AddRange(quality switch
        {
            Mp3Quality.VbrV0 => ["-q:a", "0"],
            Mp3Quality.Cbr192 => ["-b:a", "192k"],
            _ => ["-b:a", "320k"],
        });
        // Keep the source rate when MP3 supports it; otherwise high-precision resample to 44.1 kHz.
        if (info is null || !Mp3SampleRates.Contains(info.SampleRate))
            args.AddRange(["-af", "aresample=resampler=soxr:precision=28:dither_method=triangular", "-ar", "44100"]);
        // Mono stays mono (no wasted bits); surround is folded to stereo.
        if (info is null || info.Channels > 2) args.AddRange(["-ac", "2"]);
        args.AddRange(["-map_metadata", "0", "-id3v2_version", "3", "-f", "mp3"]);
        return await ConvertAsync(source, $".{quality}.mp3", args, cancellationToken);
    }

    /// <summary>Copies the MP3 audio frames unchanged and writes clean tags for car stereos.</summary>
    internal static async Task TagMp3Async(string mp3, string destination, string title, string artist,
        string albumArtist, string album, int track, int trackCount, CancellationToken cancellationToken)
    {
        List<string> args = ["-nostdin", "-loglevel", "error", "-y", "-i", mp3, "-map", "0:a:0", "-c:a", "copy",
            "-map_metadata", "0", "-id3v2_version", "3",
            "-metadata", $"title={title}", "-metadata", $"album={album}",
            "-metadata", $"track={track}/{trackCount}"];
        if (artist.Length > 0) args.AddRange(["-metadata", $"artist={artist}"]);
        if (albumArtist.Length > 0) args.AddRange(["-metadata", $"album_artist={albumArtist}"]);
        args.AddRange(["-f", "mp3", destination]);
        (int exit, string errors) = await RunAsync(args, cancellationToken);
        if (exit != 0) throw new InvalidDataException(LastLine(errors, "Could not write tags."));
    }

    /// <summary>Returns a CD-ready WAV (44.1 kHz, 16-bit, stereo) for an audio CD.</summary>
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

        return await ConvertAsync(source, ".wav",
            ["-af", "aresample=resampler=soxr:precision=28:dither_method=triangular",
             "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav"],
            cancellationToken);
    }

    private static async Task<string> ConvertAsync(string source, string suffix, IEnumerable<string> codecArgs,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(source);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..24];
        Directory.CreateDirectory(CacheDirectory);
        string output = Path.Combine(CacheDirectory, key + suffix);
        if (File.Exists(output)) return output;

        string partial = output + ".part" + Path.GetExtension(suffix);
        (int exit, string errors) = await RunAsync(
            ["-nostdin", "-loglevel", "error", "-y", "-i", source, "-map", "0:a:0", .. codecArgs, partial],
            cancellationToken);
        if (exit != 0)
        {
            File.Delete(partial);
            throw new InvalidDataException(LastLine(errors, "ffmpeg could not read this file."));
        }
        File.Move(partial, output, overwrite: true);
        return output;
    }

    private static async Task<(int ExitCode, string StandardError)> RunAsync(IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(RequireFfmpeg())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
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
        return (process.ExitCode, await errors);
    }

    private static string LastLine(string text, string fallback)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 0 ? trimmed.Split('\n')[^1].Trim() : fallback;
    }
}
