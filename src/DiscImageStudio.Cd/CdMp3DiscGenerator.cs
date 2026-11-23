using System.IO;
using System.Text;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>
/// An MP3 data CD whose burned side is almost entirely artwork: the song files sit in a thin band
/// at the outer edge, the filesystem in a hairline at the inner edge, and every sector in between
/// draws the picture. Plays on computers and MP3-capable CD players (most car stereos since ~2005).
/// </summary>
public sealed record CdMp3DiscRequest(
    string SourceImage,
    IReadOnlyList<CdMixtapeSong> Songs,
    string OutputDirectory,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    string AlbumTitle = "Mixtape",
    string Performer = "");

public sealed record CdMp3DiscSummary(
    string IsoPath,
    int SongCount,
    long FileBytes,
    double FilesStartRadiusMm,
    double DiscOuterRadiusMm,
    string PreviewPath);

public static class CdMp3DiscGenerator
{
    /// <summary>Joliet names allow 64 characters; keep room for the "01 - " prefix and extension.</summary>
    private const int MaxTitleCharacters = 52;

    /// <summary>Sectors the song files will occupy at the outer edge (exact for the ISO layout).</summary>
    public static long FileSectors(IEnumerable<string> songPaths, string? playlistText = null)
    {
        long sectors = songPaths.Sum(path => (new FileInfo(path).Length + 2047) / 2048);
        if (playlistText is not null) sectors += (Encoding.UTF8.GetByteCount(playlistText) + 2047) / 2048;
        return sectors;
    }

    /// <summary>Radius where the song band begins, for a given amount of file data.</summary>
    public static double FilesStartRadiusMm(CdDiscParameters parameters, long fileSectors)
        => parameters.RadiusFromByte(Math.Max(0, parameters.OutputSectors - fileSectors) * CdDiscParameters.BytesPerSector);

    public static CdMp3DiscSummary Generate(CdMp3DiscRequest request,
        Action<CdProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Parameters.Validate();
        request.Tone.Validate();
        if (request.Songs.Count is 0 or > 999)
            throw new InvalidDataException("Add between 1 and 999 songs.");

        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string staging = Path.Combine(Path.GetTempPath(), "MixtapeCD", "disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Numbered names keep the running order in every player.
            StringBuilder playlist = new("#EXTM3U\r\n");
            int digits = request.Songs.Count >= 100 ? 3 : 2;
            for (int i = 0; i < request.Songs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CdMixtapeSong song = request.Songs[i];
                string extension = Path.GetExtension(song.WavPath).ToLowerInvariant();
                string name = $"{(i + 1).ToString().PadLeft(digits, '0')} - {SafeName(song.Title)}{extension}";
                File.Copy(song.WavPath, Path.Combine(staging, name));
                playlist.Append(name).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(staging, "playlist.m3u"), playlist.ToString(), new UTF8Encoding(false));

            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(staging, request.Parameters,
                VolumeLabel(request.AlbumTitle), filesAtOuterRing: true);
            string iso = Path.Combine(outputDirectory, SafeName(request.AlbumTitle) + ".iso");
            CdDataImageGenerator.Generate(request.SourceImage, iso, request.Parameters, plan, progress,
                cancellationToken, request.Tone);

            double filesStart = request.Parameters.RadiusFromByte((long)plan.FileDataStartLba * CdDiscParameters.BytesPerSector);
            string previewPath = Path.Combine(outputDirectory, "disc-preview.png");
            byte[] pixels = RenderDiscPreview(request.SourceImage, request.Parameters, request.Tone,
                plan.MetadataEndLbaExclusive, plan.FileDataStartLba, 1200, cancellationToken);
            ImageIO.RasterCodec.SavePng(previewPath, 1200, 1200, pixels, grayscale: false);
            return new CdMp3DiscSummary(iso, request.Songs.Count, plan.FileBytes, filesStart,
                request.Parameters.OutputOuterRadiusMm, previewPath);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Photo-style render of the burned side: art from <paramref name="drawStartSector"/> up to
    /// <paramref name="filesStartSector"/>, plain data texture elsewhere.</summary>
    public static byte[] RenderDiscPreview(string sourceImage, CdDiscParameters parameters, ToneProfile tone,
        long drawStartSector, long filesStartSector, int size, CancellationToken cancellationToken = default)
        => CdDiscRenderer.Render(sourceImage, parameters, tone.Normalized(),
            Math.Max(0, drawStartSector) * CdDiscParameters.BytesPerSector,
            Math.Clamp(filesStartSector, 0, parameters.OutputSectors) * CdDiscParameters.BytesPerSector,
            size, cancellationToken);

    private static string VolumeLabel(string title)
    {
        string label = new(title.ToUpperInvariant().Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : '_').ToArray());
        label = label.Trim('_');
        return label.Length == 0 ? "MIXTAPE" : label.Length > 16 ? label[..16] : label;
    }

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(c => invalid.Contains(c) || c is '"' or '*' or '?' or ':' ? '_' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length == 0) cleaned = "Track";
        return cleaned.Length > MaxTitleCharacters ? cleaned[..MaxTitleCharacters].TrimEnd() : cleaned;
    }
}
