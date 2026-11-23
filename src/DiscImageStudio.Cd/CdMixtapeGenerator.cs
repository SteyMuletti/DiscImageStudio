using System.IO;
using System.Text;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Cd;

/// <summary>A mixtape disc: real music tracks on the inner ring, then a picture track that
/// fills the rest of the disc so the artwork shows on the outer ring.</summary>
public sealed record CdMixtapeRequest(
    string SourceImage,
    string MusicDirectory,
    string OutputDirectory,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    bool Interleave = true,
    double GapSeconds = 0,
    string AlbumTitle = "Mixtape",
    string Performer = "",
    string PictureTrackTitle = "Disc Art (noise - do not play)")
{
    /// <summary>Explicit song order and titles; when set, <see cref="MusicDirectory"/> is ignored.</summary>
    public IReadOnlyList<CdMixtapeSong>? Songs { get; init; }
}

/// <summary>One CD-ready WAV (44.1 kHz/16-bit/stereo PCM) and the title shown in players.</summary>
public sealed record CdMixtapeSong(string WavPath, string Title);

public sealed record CdMixtapeTrack(int Number, string SourcePath, string OutputPath, long Sectors)
{
    public string Title { get; init; } = Path.GetFileNameWithoutExtension(SourcePath);
}

public sealed record CdMixtapeSummary(
    IReadOnlyList<CdMixtapeTrack> MusicTracks,
    string PictureTrackPath,
    long PictureSectors,
    long MusicSectors,
    double MusicOuterRadiusMm,
    double PictureOuterRadiusMm,
    string CueSheetPath,
    string PreviewPath);

public static class CdMixtapeGenerator
{
    /// <summary>Red Book minimum track length (4 seconds).</summary>
    public const long MinimumTrackSectors = 4 * 75;

    private static readonly string[] SupportedExtensions = [".wav"];

    /// <summary>Music files in name order (01-..., 02-...). Only 44.1 kHz/16-bit/stereo PCM WAV.</summary>
    public static IReadOnlyList<string> ListMusic(string musicDirectory)
    {
        string directory = Path.GetFullPath(musicDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Music folder not found: {directory}");
        string[] files = Directory.EnumerateFiles(directory)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidDataException(
                $"No .wav files in {directory}. Convert MP3/FLAC first (see tools/mixtape/convert-to-cd-wav.sh).");
        if (files.Length > 98)
            throw new InvalidDataException("A CD holds at most 99 tracks (98 songs + the picture track).");
        return files;
    }

    /// <summary>Sectors each song occupies on disc, including padding and the optional gap.</summary>
    public static long[] MeasureMusic(IReadOnlyList<string> files, double gapSeconds)
    {
        long gapSectors = GapSectors(gapSeconds);
        long[] sectors = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            using FileStream input = File.OpenRead(files[i]);
            (_, long length) = ReadRange(input, files[i]);
            long songSectors = (length + CdDiscParameters.BytesPerSector - 1) / CdDiscParameters.BytesPerSector;
            // The gap is appended to every song (before the next one / before the picture).
            sectors[i] = Math.Max(MinimumTrackSectors, songSectors) + gapSectors;
        }
        return sectors;
    }

    public static CdMixtapeSummary Generate(CdMixtapeRequest request,
        Action<CdProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CdDiscParameters parameters = request.Parameters;
        parameters.Validate();
        request.Tone.Validate();
        if (!double.IsFinite(request.GapSeconds) || request.GapSeconds is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(request), "--gap must be 0..10 seconds.");

        IReadOnlyList<CdMixtapeSong> songs = request.Songs
            ?? ListMusic(request.MusicDirectory).Select(f => new CdMixtapeSong(f, SongTitle(f))).ToArray();
        if (songs.Count is 0 or > 98)
            throw new InvalidDataException("A mixtape needs 1 to 98 songs (the picture takes the last track).");
        IReadOnlyList<string> music = songs.Select(song => song.WavPath).ToArray();
        long[] musicSectors = MeasureMusic(music, request.GapSeconds);
        long totalMusic = musicSectors.Sum();
        long pictureSectors = parameters.OutputSectors - totalMusic;
        if (pictureSectors < MinimumTrackSectors)
        {
            double musicMinutes = totalMusic / 75.0 / 60.0;
            double discMinutes = parameters.OutputSectors / 75.0 / 60.0;
            throw new InvalidDataException(
                $"The music is {musicMinutes:F1} min but the disc holds {discMinutes:F1} min; "
                + "there is no room left for the picture. Drop some songs or use an 80-minute disc.");
        }

        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string sourceImage = Path.GetFullPath(request.SourceImage);

        List<CdMixtapeTrack> tracks = [];
        for (int i = 0; i < music.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int number = i + 1;
            string name = $"{number:00} - {SafeName(songs[i].Title)}.wav";
            string output = Path.Combine(outputDirectory, name);
            if (Path.GetFullPath(output).Equals(Path.GetFullPath(music[i]), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output folder must differ from the music folder.");
            WriteMusicTrack(music[i], output, musicSectors[i] * CdDiscParameters.BytesPerSector);
            tracks.Add(new CdMixtapeTrack(number, music[i], output, musicSectors[i]) { Title = songs[i].Title });
        }

        int pictureNumber = music.Count + 1;
        string picturePath = Path.Combine(outputDirectory, $"{pictureNumber:00} - disc-art.wav");
        WritePictureTrack(sourceImage, picturePath, parameters, totalMusic, pictureSectors,
            request.Interleave, request.Tone, progress, cancellationToken);

        string cuePath = Path.Combine(outputDirectory, "mixtape.cue");
        WriteCue(cuePath, request, tracks, picturePath, pictureNumber);

        double musicOuter = parameters.RadiusFromByte(totalMusic * CdDiscParameters.BytesPerSector);
        double pictureOuter = parameters.OutputOuterRadiusMm;
        string previewPath = Path.Combine(outputDirectory, "mixtape-preview.png");
        WritePreview(sourceImage, previewPath, parameters, musicOuter, pictureOuter);

        CdTrackMetadata.Save(picturePath, new CdTrackMetadata(
            sourceImage, picturePath, parameters.InnerRadiusMm, parameters.OuterRadiusMm, parameters.Sectors,
            parameters.LinearVelocityMmPerSecond, parameters.StartAngleRadians * 180.0 / Math.PI,
            parameters.ImageOuterRadiusMm, request.Interleave, request.Tone.DitherStrength, request.Tone.Levels,
            request.Tone.Brightness, request.Tone.SrgbLinearization, parameters.PitchLinear,
            parameters.PitchQuadratic, parameters.PitchCubic, parameters.UsableSectors,
            parameters.SourceImageOuterRadiusMm));

        return new CdMixtapeSummary(tracks, picturePath, pictureSectors, totalMusic, musicOuter, pictureOuter,
            cuePath, previewPath);
    }

    /// <summary>Blank disc choices for <see cref="DiscParameters"/>.</summary>
    public static IReadOnlyList<(string Id, string Name)> DiscTypes { get; } =
    [
        ("verbatim-azo", "Verbatim AZO CD-R 80 min (DataLifePlus / Silver Inkjet Printable)"),
        ("cd-80", "Generic CD-R 80 min"),
        ("cd-74", "Generic CD-R 74 min"),
    ];

    /// <summary>Measured geometry for a blank disc. "verbatim-azo" is Verbatim's AZO-dye 80-min
    /// CD-R (DataLifePlus, including the silver inkjet printable line).</summary>
    public static CdDiscParameters DiscParameters(string disc)
    {
        string id = disc.ToLowerInvariant() switch
        {
            "verbatim-azo" or "verbatim" => "verbatim-azo-43438",
            string other => other,
        };
        CdDiscPresetDefinition preset = DiscPresetJsonStore.LoadBuiltIn().CdPresets
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown disc '{disc}'. Use verbatim-azo, cd-80 or cd-74.");
        // The artwork spans the whole disc out to the measured edge.
        return new CdDiscParameters(preset.InnerRadiusMm, preset.OuterRadiusMm, preset.Sectors,
            ImageOuterRadiusMm: preset.OuterRadiusMm,
            PitchLinear: preset.PitchLinear, PitchQuadratic: preset.PitchQuadratic, PitchCubic: preset.PitchCubic,
            UsableSectors: CdDiscPreset.ResolveUsableSectors(preset.Sectors, preset.UsableSectors));
    }

    /// <summary>Exact on-disc length of one song (sector padding, 4 s minimum and gap included).</summary>
    public static long SongSectors(string wavPath, double gapSeconds) => MeasureMusic([wavPath], gapSeconds)[0];

    /// <summary>
    /// Photo-style render of the burned side (BGRA, opaque, size×size). The art ring uses the same
    /// byte→disc projection as generation; the music ring shows plain audio texture.
    /// </summary>
    public static byte[] RenderDiscPreview(string sourceImage, CdDiscParameters parameters, ToneProfile tone,
        long musicSectors, int size, CancellationToken cancellationToken = default)
        => CdDiscRenderer.Render(sourceImage, parameters, tone.Normalized(),
            Math.Clamp(musicSectors, 0, parameters.OutputSectors) * CdDiscParameters.BytesPerSector,
            parameters.TotalBytes, size, cancellationToken);

    /// <summary>Radius (mm) where the music ends and the picture begins.</summary>
    public static double PictureStartRadiusMm(CdDiscParameters parameters, long musicSectors)
        => parameters.RadiusFromByte(musicSectors * CdDiscParameters.BytesPerSector);

    private static long GapSectors(double gapSeconds) => (long)Math.Round(gapSeconds * 75.0);

    private static (long Offset, long Length) ReadRange(Stream input, string path)
    {
        try
        {
            return CdWaveFile.ReadAudioRange(input);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)}: {error.Message} Convert it with tools/mixtape/convert-to-cd-wav.sh.", error);
        }
    }

    private static void WriteMusicTrack(string source, string output, long trackBytes)
    {
        using FileStream input = File.OpenRead(source);
        (long offset, long length) = ReadRange(input, source);
        using FileStream file = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        CdWaveFile.WriteHeader(file, trackBytes);
        input.Position = offset;
        byte[] buffer = new byte[1 << 20];
        long remaining = length;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException($"{source} ended early.");
            file.Write(buffer, 0, read);
            remaining -= read;
        }
        // Silence pads the song to a whole sector, the 4 s minimum, and the requested gap.
        long silence = trackBytes - length;
        Array.Clear(buffer);
        while (silence > 0)
        {
            int count = (int)Math.Min(buffer.Length, silence);
            file.Write(buffer, 0, count);
            silence -= count;
        }
    }

    /// <summary>
    /// Generates the whole-disc picture exactly as cd-generate would and keeps only the part
    /// after the music. In disc-at-once mode the drive's CIRC encoder runs continuously across
    /// tracks, so the physical pits match the full-disc design outside the music ring.
    /// </summary>
    private static void WritePictureTrack(string sourceImage, string output, CdDiscParameters parameters,
        long skipSectors, long pictureSectors, bool interleave, ToneProfile tone,
        Action<CdProgress>? progress, CancellationToken cancellationToken)
    {
        long pictureBytes = pictureSectors * CdDiscParameters.BytesPerSector;
        CdWaveFile.ValidateLength(pictureBytes);
        using FileStream file = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
            FileOptions.SequentialScan);
        CdWaveFile.WriteHeader(file, pictureBytes);
        using SkipStream sink = new(file, skipSectors * CdDiscParameters.BytesPerSector);
        CdTrackGenerator.GenerateToStream(sourceImage, sink, parameters, interleave, progress, cancellationToken,
            output, CdAudioByteOrder.LittleEndian, tone);
        if (sink.Written != pictureBytes)
            throw new InvalidOperationException($"Picture track length mismatch: {sink.Written} != {pictureBytes}.");
    }

    private static void WriteCue(string cuePath, CdMixtapeRequest request, IReadOnlyList<CdMixtapeTrack> tracks,
        string picturePath, int pictureNumber)
    {
        StringBuilder cue = new();
        if (!string.IsNullOrWhiteSpace(request.Performer)) cue.Append($"PERFORMER \"{CueText(request.Performer)}\"\r\n");
        cue.Append($"TITLE \"{CueText(request.AlbumTitle)}\"\r\n");
        foreach (CdMixtapeTrack track in tracks)
        {
            cue.Append($"FILE \"{Path.GetFileName(track.OutputPath)}\" WAVE\r\n");
            cue.Append($"  TRACK {track.Number:00} AUDIO\r\n");
            cue.Append($"    TITLE \"{CueText(track.Title)}\"\r\n");
            cue.Append("    INDEX 01 00:00:00\r\n");
        }
        cue.Append($"FILE \"{Path.GetFileName(picturePath)}\" WAVE\r\n");
        cue.Append($"  TRACK {pictureNumber:00} AUDIO\r\n");
        cue.Append($"    TITLE \"{CueText(request.PictureTrackTitle)}\"\r\n");
        cue.Append("    INDEX 01 00:00:00\r\n");
        File.WriteAllText(cuePath, cue.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Source image as it will appear on the disc: music ring tinted, unused areas dark.</summary>
    private static void WritePreview(string sourceImage, string previewPath, CdDiscParameters parameters,
        double musicOuterMm, double pictureOuterMm)
    {
        const int size = 1200;
        RasterImage image = RasterImage.Load(sourceImage);
        double imageOuter = parameters.SamplingImageOuterRadiusMm;
        double mmPerPixel = imageOuter / (size / 2.0);
        double scale = image.Width / (double)size;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x + 0.5 - size / 2.0, dy = y + 0.5 - size / 2.0;
            double r = Math.Sqrt(dx * dx + dy * dy) * mmPerPixel;
            int o = (y * size + x) * 4;
            byte red, green, blue;
            if (r < parameters.InnerRadiusMm || r > pictureOuterMm)
            {
                red = green = blue = 40; // hub / unwritten edge
            }
            else if (r < musicOuterMm)
            {
                red = 70; green = 90; blue = 140; // music ring: plain audio texture, no artwork
            }
            else
            {
                byte g = image.GrayAt((x + 0.5) * scale, (y + 0.5) * scale);
                red = green = blue = g;
            }
            // RasterCodec expects BGRA.
            pixels[o] = blue; pixels[o + 1] = green; pixels[o + 2] = red; pixels[o + 3] = 255;
        }
        RasterCodec.SavePng(previewPath, size, size, pixels, grayscale: false);
    }

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) || c == '"' ? '_' : c).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    /// <summary>"01 - Song.wav" → "Song": the ordering prefix is only for sorting.</summary>
    public static string SongTitle(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string trimmed = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\d{1,3}\s*[-_.)]*\s*", "");
        return trimmed.Length > 0 ? trimmed : name;
    }

    private static string CueText(string text) => text.Replace('"', '\'');

    /// <summary>Forward-only sink that drops the first N bytes (the music region).</summary>
    private sealed class SkipStream(Stream inner, long skip) : Stream
    {
        private long _toSkip = skip;
        internal long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_toSkip > 0)
            {
                int dropped = (int)Math.Min(_toSkip, buffer.Length);
                _toSkip -= dropped;
                buffer = buffer[dropped..];
            }
            if (buffer.IsEmpty) return;
            inner.Write(buffer);
            Written += buffer.Length;
        }
    }
}
