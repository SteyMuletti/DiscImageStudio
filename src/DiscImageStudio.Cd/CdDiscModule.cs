using System.Globalization;
using System.IO;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

public sealed class CdDiscModule : IOpticalDiscModule
{
    public const string ModuleId = "cd-da";

    private static readonly IReadOnlyList<DiscOptionDefinition> GeometryOptions =
    [
        new("r0", "Inner radius", DiscOptionValueType.Decimal, DefaultValue: "24.5", Unit: "mm"),
        new("r1", "Outer radius", DiscOptionValueType.Decimal, DefaultValue: "56.8", Unit: "mm"),
        new("sectors", "Geometry sectors", DiscOptionValueType.Integer, DefaultValue: "359849"),
        new("usable-sectors", "Output sectors within the geometry", DiscOptionValueType.Integer),
        new("velocity", "Linear velocity", DiscOptionValueType.Decimal, DefaultValue: "1200", Unit: "mm/s"),
        new("theta0", "Start angle", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "degrees"),
        new("outer", "Image outer radius", DiscOptionValueType.Decimal, DefaultValue: "57.5", Unit: "mm"),
        new("pitch-linear", "Relative linear pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
        new("pitch-quadratic", "Relative quadratic pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
        new("pitch-cubic", "Relative cubic pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
    ];

    /// <summary>Second parameter set of the geometry preview: the disc the reader follows.
    /// Radii and pitch shape may differ from the generated geometry.</summary>
    private static readonly IReadOnlyList<DiscOptionDefinition> MeasuredGeometryOptions =
    [
        new("actual-r0", "Measured inner radius", DiscOptionValueType.Decimal, DefaultValue: "24.3", Unit: "mm"),
        new("actual-r1", "Measured outer radius", DiscOptionValueType.Decimal, DefaultValue: "56.6", Unit: "mm"),
        new("actual-pitch-linear", "Measured relative linear pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
        new("actual-pitch-quadratic", "Measured relative quadratic pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
        new("actual-pitch-cubic", "Measured relative cubic pitch", DiscOptionValueType.Decimal, DefaultValue: "0"),
    ];

    /// <summary>Tone mapping shared by generation and the source-projection preview. The
    /// defaults reproduce the pre-tone hard threshold exactly.</summary>
    private static readonly IReadOnlyList<DiscOptionDefinition> ToneOptions =
    [
        new("tone-dither", "Dither strength", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "0..1"),
        new("tone-levels", "Palette levels", DiscOptionValueType.Integer, DefaultValue: "2", Unit: "2 or 4"),
        new("tone-brightness", "Tone brightness", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "-64..64"),
        new("tone-gamma", "sRGB linearization", DiscOptionValueType.Boolean, DefaultValue: "false"),
    ];

    private static readonly DiscModuleDescriptor ModuleDescriptor = new(
        ModuleId,
        "CD",
        OpticalDiscFamily.CompactDisc,
        "CD audio tracks or Mode 1 ISO9660/Joliet data images with CLV geometry previews.",
        SupportsCancellation: true,
        Commands:
        [
            new DiscCommandDescriptor(
                "cd-generate",
                "Generate CD audio or data image",
                "Without files, writes WAV/CUE or RAW audio; with --data-dir, writes a Mode 1 ISO with inner/outer-ring files.",
                DiscModuleCapabilities.RawTrackGeneration | DiscModuleCapabilities.DataImageGeneration | DiscModuleCapabilities.HybridFilesystem,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Audio track (.wav/.raw) or data image (.iso)", DiscOptionValueType.OutputFile, Required: true),
                    new("data-dir", "Files to include; omitted for audio", DiscOptionValueType.InputFolder),
                    new("volume-label", "Data volume label", DiscOptionValueType.String, DefaultValue: "CD_IMAGE"),
                    new("file-position", "File position", DiscOptionValueType.Choice, DefaultValue: "inner", Choices: ["inner", "outer"]),
                    .. GeometryOptions,
                    new("interleave", "CD-DA delay interleave", DiscOptionValueType.Boolean, DefaultValue: "true"),
                    new("cue", "Write a CUE sheet next to the WAV track", DiscOptionValueType.Boolean, DefaultValue: "true"),
                    .. ToneOptions,
                ]),
            new DiscCommandDescriptor(
                "cd-mixtape",
                "Generate mixtape audio CD with disc art",
                "Writes your songs as normal CD audio tracks on the inner ring, then a picture track that draws the image on the outer ring, plus a multi-track CUE.",
                DiscModuleCapabilities.RawTrackGeneration,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("music-dir", "Folder of 44.1 kHz 16-bit stereo WAV songs, burned in file-name order", DiscOptionValueType.InputFolder, Required: true),
                    new("output-dir", "Folder for the track WAVs, mixtape.cue and preview", DiscOptionValueType.String, Required: true),
                    new("gap", "Silence after each song", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "seconds"),
                    new("disc", "Blank disc type (sets geometry; r0/r1/sectors override it)", DiscOptionValueType.Choice, DefaultValue: "verbatim-azo",
                        Choices: ["verbatim-azo", "cd-80", "cd-74"]),
                    new("title", "Album title (CUE/CD-Text)", DiscOptionValueType.String, DefaultValue: "Mixtape"),
                    new("performer", "Album artist (CUE/CD-Text)", DiscOptionValueType.String, DefaultValue: ""),
                    .. GeometryOptions,
                    new("interleave", "CD-DA delay interleave", DiscOptionValueType.Boolean, DefaultValue: "true"),
                    .. ToneOptions,
                ]),
            new DiscCommandDescriptor(
                "cd-mp3",
                "Generate MP3 data CD with full-disc art",
                "Puts your MP3 files (copied as-is, in file-name order) in a thin band at the outer edge and draws the image over the rest of the disc. Writes an .iso to burn as a data CD.",
                DiscModuleCapabilities.DataImageGeneration,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("music-dir", "Folder of MP3 files, burned in file-name order", DiscOptionValueType.InputFolder, Required: true),
                    new("output-dir", "Folder for the .iso and preview", DiscOptionValueType.String, Required: true),
                    new("disc", "Blank disc type (sets geometry; r0/r1/sectors override it)", DiscOptionValueType.Choice, DefaultValue: "verbatim-azo",
                        Choices: ["verbatim-azo", "cd-80", "cd-74"]),
                    new("title", "Album title (disc name)", DiscOptionValueType.String, DefaultValue: "Mixtape"),
                    .. GeometryOptions,
                    .. ToneOptions,
                ]),
            new DiscCommandDescriptor(
                "cd-preview-warp",
                "Preview CD geometry",
                "Projects generated geometry onto measured disc geometry.",
                DiscModuleCapabilities.GeometryPreview,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                    .. GeometryOptions,
                    .. MeasuredGeometryOptions,
                    .. ToneOptions,
                    new("data-dir", "Data files for drawing-range preview", DiscOptionValueType.InputFolder),
                    new("volume-label", "Data volume label", DiscOptionValueType.String, DefaultValue: "CD_IMAGE"),
                    new("file-position", "File position", DiscOptionValueType.Choice, DefaultValue: "inner", Choices: ["inner", "outer"]),
                    new("size", "Preview size", DiscOptionValueType.Integer, DefaultValue: "1600", Unit: "px"),
                    new("samples-per-sector", "Samples per sector", DiscOptionValueType.Integer, DefaultValue: "16"),
                ]),
            new DiscCommandDescriptor(
                "cd-preview-track",
                "Preview CD audio or data image",
                "Renders WAV/RAW audio or a Mode 1 ISO with measured geometry.",
                DiscModuleCapabilities.EncodedOutputPreview,
                [
                    new("track", "WAV/RAW track or Mode 1 ISO", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                    .. GeometryOptions,
                    new("size", "Preview size", DiscOptionValueType.Integer, DefaultValue: "1600", Unit: "px"),
                    new("byte-step", "Byte sampling interval", DiscOptionValueType.Integer, DefaultValue: "48"),
                    new("deinterleave", "Undo CD-DA delay interleave", DiscOptionValueType.Boolean, DefaultValue: "true"),
                ]),
        ]);

    public DiscModuleDescriptor Descriptor => ModuleDescriptor;

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        CdCommandOptions options = CdCommandOptions.Parse(request.Arguments);
        DiscJobResult result = request.Command switch
        {
            "cd-generate" => Generate(request, options, progress, cancellationToken),
            "cd-mixtape" => Mixtape(request, options, progress, cancellationToken),
            "cd-mp3" => Mp3Disc(request, options, progress, cancellationToken),
            "cd-preview-warp" => PreviewWarp(request, options, progress, cancellationToken),
            "cd-preview-track" => PreviewTrack(request, options, progress, cancellationToken),
            _ => throw new ArgumentException(
                $"CD module does not support command '{request.Command}'.",
                nameof(request)),
        };
        return Task.FromResult(result);
    }

    private static DiscJobResult Generate(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        CdImageRequest image = new(
            options.Require("input"),
            options.Require("output"),
            ReadParameters(options),
            ReadTone(options),
            options.GetBoolean("interleave", true),
            options.GetBoolean("cue", true),
            options.GetString("data-dir", string.Empty),
            options.GetString("volume-label", "CD_IMAGE"),
            ReadFilePosition(options));
        DiscImageResult result = CdImageGenerator.Generate(image, progress, cancellationToken);
        return new DiscJobResult(ModuleId, request.Command, 0, result.Summary, result.OutputPath);
    }

    private static DiscJobResult Mixtape(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        CdMixtapeRequest mixtape = new(
            options.Require("input"),
            options.Require("music-dir"),
            options.Require("output-dir"),
            ReadParameters(options, DiscPreset(options.GetString("disc", "verbatim-azo"))),
            ReadTone(options),
            options.GetBoolean("interleave", true),
            options.GetDouble("gap", 0),
            options.GetString("title", "Mixtape"),
            options.GetString("performer", string.Empty));
        CdMixtapeSummary summary = CdMixtapeGenerator.Generate(mixtape,
            value => progress?.Report(new DiscJobProgress(ModuleId, "generate",
                $"Disc art progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                value.TotalBytes, value.Elapsed)), cancellationToken);
        static string Minutes(long sectors) => TimeSpan.FromSeconds(sectors / 75.0).ToString(@"mm\:ss");
        string message =
            $"Mixtape complete: {summary.MusicTracks.Count} songs ({Minutes(summary.MusicSectors)}), "
            + $"disc art track {summary.MusicTracks.Count + 1} ({Minutes(summary.PictureSectors)}). "
            + $"Artwork visible from {summary.MusicOuterRadiusMm:F1} mm to {summary.PictureOuterRadiusMm:F1} mm. "
            + $"cue={summary.CueSheetPath}, preview={summary.PreviewPath}";
        return new DiscJobResult(ModuleId, request.Command, 0, message, summary.CueSheetPath);
    }

    private static DiscJobResult Mp3Disc(DiscJobRequest request, CdCommandOptions options,
        IProgress<DiscJobProgress>? progress, CancellationToken cancellationToken)
    {
        string musicDir = Path.GetFullPath(options.Require("music-dir"));
        CdMixtapeSong[] songs = Directory.EnumerateFiles(musicDir)
            .Where(f => Path.GetExtension(f).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new CdMixtapeSong(f, CdMixtapeGenerator.SongTitle(f))).ToArray();
        if (songs.Length == 0) throw new ArgumentException($"No .mp3 files in {musicDir}.");
        CdMp3DiscSummary summary = CdMp3DiscGenerator.Generate(new CdMp3DiscRequest(options.Require("input"), songs,
            options.Require("output-dir"), ReadParameters(options, DiscPreset(options.GetString("disc", "verbatim-azo"))),
            ReadTone(options), options.GetString("title", "Mixtape")),
            value => progress?.Report(new DiscJobProgress(ModuleId, "generate", $"Disc progress {value.Fraction:P1}",
                value.Fraction, value.CompletedBytes, value.TotalBytes, value.Elapsed)), cancellationToken);
        string message = $"MP3 disc complete: {summary.SongCount} songs ({summary.FileBytes / 1048576.0:F0} MB). "
            + $"Art from the hub out to {summary.FilesStartRadiusMm:F1} mm; songs in the band to {summary.DiscOuterRadiusMm:F1} mm. "
            + $"iso={summary.IsoPath}, preview={summary.PreviewPath}";
        return new DiscJobResult(ModuleId, request.Command, 0, message, summary.IsoPath);
    }

    private static CdDiscParameters DiscPreset(string name) => CdMixtapeGenerator.DiscParameters(name);

    private static DiscJobResult PreviewWarp(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string output = Path.GetFullPath(options.Require("output"));
        progress?.Report(new DiscJobProgress(ModuleId, "preview", "Rendering CD geometry preview…"));
        CdDiscParameters generated = ReadParameters(options);
        string dataDirectory = options.GetString("data-dir", string.Empty).Trim();
        bool outer = ReadFilePosition(options);
        if (dataDirectory.Length > 0)
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(dataDirectory, generated,
                options.GetString("volume-label", "CD_IMAGE"), outer);
            CdDataImageGenerator.PreviewWarp(options.Require("input"), output, generated,
                ReadMeasuredParameters(options, generated), options.GetInt("size", 1600),
                options.GetInt("samples-per-sector", 16), ReadTone(options), plan, cancellationToken);
            return new DiscJobResult(ModuleId, request.Command, 0,
                $"CD data geometry preview complete: output={output}", output);
        }
        CdTrackGenerator.PreviewWarp(
            options.Require("input"),
            output,
            generated,
            ReadMeasuredParameters(options, generated),
            options.GetInt("size", 1600),
            options.GetInt("samples-per-sector", 16),
            ReadTone(options),
            cancellationToken);
        return new DiscJobResult(
            ModuleId,
            request.Command,
            0,
            $"CD geometry preview complete: output={output}",
            output);
    }

    private static DiscJobResult PreviewTrack(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string output = Path.GetFullPath(options.Require("output"));
        progress?.Report(new DiscJobProgress(ModuleId, "preview", "Rendering CD track preview…"));
        string track = options.Require("track");
        if (Path.GetExtension(track).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            CdDataImageMetadata? metadata = CdDataImageMetadata.TryLoad(track);
            CdDiscParameters parameters = ReadParameters(options, metadata?.Parameters);
            CdDataImageGenerator.PreviewTrack(track, output, parameters,
                options.GetInt("size", 1600), options.GetInt("byte-step", 48), cancellationToken);
            return new DiscJobResult(ModuleId, request.Command, 0,
                $"CD data readback preview complete: output={output}", output);
        }
        CdTrackGenerator.PreviewTrack(
            options.Require("track"),
            output,
            ReadParameters(options, CdTrackMetadata.TryLoad(track)?.CreateParameters()),
            options.GetInt("size", 1600),
            options.GetInt("byte-step", 48),
            options.GetBoolean("deinterleave", true),
            cancellationToken);
        return new DiscJobResult(
            ModuleId,
            request.Command,
            0,
            $"CD track preview complete: output={output}",
            output);
    }

    private static CdDiscParameters ReadParameters(CdCommandOptions options, CdDiscParameters? fallback = null)
    {
        double startAngleDegrees = options.GetDouble("theta0", (fallback?.StartAngleRadians ?? 0) * 180.0 / Math.PI);
        return new CdDiscParameters(
            options.GetDouble("r0", fallback?.InnerRadiusMm ?? 24.5),
            options.GetDouble("r1", fallback?.OuterRadiusMm ?? 56.8),
            options.GetLong("sectors", fallback?.Sectors ?? 359849),
            options.GetDouble("velocity", fallback?.LinearVelocityMmPerSecond ?? CdDiscParameters.StandardLinearVelocityMmPerSecond),
            startAngleDegrees * Math.PI / 180.0,
            options.GetDouble("outer", fallback?.ImageOuterRadiusMm ?? CdDiscParameters.StandardImageOuterRadiusMm),
            options.GetDouble("pitch-linear", fallback?.PitchLinear ?? 0),
            options.GetDouble("pitch-quadratic", fallback?.PitchQuadratic ?? 0),
            options.GetDouble("pitch-cubic", fallback?.PitchCubic ?? 0),
            options.Contains("usable-sectors") ? options.GetLong("usable-sectors", 0)
                : options.Contains("sectors") ? null : fallback?.UsableSectors,
            options.Contains("sectors") || options.Contains("usable-sectors") ? null : fallback?.SourceImageOuterRadiusMm);
    }

    private static bool ReadFilePosition(CdCommandOptions options)
        => options.GetString("file-position", "inner").ToLowerInvariant() switch
        {
            "inner" => false,
            "outer" => true,
            _ => throw new ArgumentException("--file-position must be inner or outer."),
        };

    private static ToneProfile ReadTone(CdCommandOptions options)
    {
        ToneProfile tone = new(
            options.GetDouble("tone-dither", 0.0),
            options.GetInt("tone-levels", ToneProfile.TwoLevels),
            options.GetDouble("tone-brightness", 0.0),
            options.GetBoolean("tone-gamma", false));
        tone.Validate();
        return tone;
    }

    /// <summary>Measured set of the geometry preview: the program it reads is the generated
    /// one, with independently measured radii and pitch coefficients.</summary>
    private static CdDiscParameters ReadMeasuredParameters(
        CdCommandOptions options,
        CdDiscParameters generated)
        => generated with
        {
            InnerRadiusMm = options.GetDouble("actual-r0", 24.3),
            OuterRadiusMm = options.GetDouble("actual-r1", 56.6),
            PitchLinear = options.GetDouble("actual-pitch-linear", 0),
            PitchQuadratic = options.GetDouble("actual-pitch-quadratic", 0),
            PitchCubic = options.GetDouble("actual-pitch-cubic", 0),
        };

    private sealed class CdCommandOptions
    {
        private readonly Dictionary<string, string> _values;

        private CdCommandOptions(Dictionary<string, string> values) => _values = values;

        internal static CdCommandOptions Parse(IReadOnlyList<string> arguments)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{argument}'.");
                }

                int equals = argument.IndexOf('=');
                string key;
                string value;
                if (equals >= 0)
                {
                    key = argument[2..equals];
                    value = argument[(equals + 1)..];
                }
                else
                {
                    key = argument[2..];
                    if (++index >= arguments.Count)
                    {
                        throw new ArgumentException($"Missing value for --{key}.");
                    }

                    value = arguments[index];
                }

                if (!values.TryAdd(key, value))
                {
                    throw new ArgumentException($"Option --{key} was specified more than once.");
                }
            }

            return new CdCommandOptions(values);
        }

        internal bool Contains(string key) => _values.ContainsKey(key);

        internal string Require(string key)
            => _values.TryGetValue(key, out string? value)
                ? value
                : throw new ArgumentException($"Missing required option --{key}.");

        internal string GetString(string key, string fallback)
            => _values.TryGetValue(key, out string? value) ? value : fallback;

        internal int GetInt(string key, int fallback)
            => _values.TryGetValue(key, out string? value)
                ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : fallback;

        internal long GetLong(string key, long fallback)
            => _values.TryGetValue(key, out string? value)
                ? long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : fallback;

        internal double GetDouble(string key, double fallback)
            => _values.TryGetValue(key, out string? value)
                ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
                : fallback;

        internal bool GetBoolean(string key, bool fallback)
        {
            if (!_values.TryGetValue(key, out string? value))
            {
                return fallback;
            }

            return value switch
            {
                "1" => true,
                "0" => false,
                _ => bool.Parse(value),
            };
        }
    }
}
