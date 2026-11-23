using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

namespace MixtapeCd.App;

public sealed partial class MainWindow : Window
{
    private static readonly ToneProfile Tone = new(0.75, ToneProfile.TwoLevels, 0, false);
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private const int PreviewSize = 900;

    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _previewCancel;
    private string? _artSource; // what the user picked
    private string? _artPath;   // square, disc-centred PNG the engine uses
    private Bitmap? _artSquare;
    private string? _builtCue; // .cue (audio CD) or .iso (MP3 disc) ready for ImgBurn
    private bool _building;

    public ObservableCollection<Track> Tracks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        ArtFit.Items.Add(new ComboBoxItem { Content = "Fill the disc (crop the edges)", Tag = false });
        ArtFit.Items.Add(new ComboBoxItem { Content = "Show the whole image (black borders)", Tag = true });
        ArtFit.SelectedIndex = 0;
        DiscMode.Items.Add(new ComboBoxItem { Content = "MP3 disc: art covers the whole disc", Tag = true });
        DiscMode.Items.Add(new ComboBoxItem { Content = "Audio CD: art on the outer ring only", Tag = false });
        DiscMode.SelectedIndex = 0;
        Quality.Items.Add(new ComboBoxItem { Content = "Best: MP3 320 kbps (recommended)", Tag = Mp3Quality.Best320 });
        Quality.Items.Add(new ComboBoxItem { Content = "Very high: MP3 VBR V0 (~245 kbps, smaller)", Tag = Mp3Quality.VbrV0 });
        Quality.Items.Add(new ComboBoxItem { Content = "Good: MP3 192 kbps (smallest)", Tag = Mp3Quality.Cbr192 });
        Quality.SelectedIndex = 0;
        foreach ((string id, string name) in CdMixtapeGenerator.DiscTypes)
            DiscType.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        DiscType.SelectedIndex = 0;
        foreach ((string label, double seconds) in new[] { ("No gap (seamless)", 0.0), ("1 second", 1), ("2 seconds", 2) })
            Gap.Items.Add(new ComboBoxItem { Content = label, Tag = seconds });
        Gap.SelectedIndex = 0;

        foreach (string art in BuiltInArtFiles())
            BuiltInArt.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(art), Tag = art });
        UpdateModeUi();
        SetDefaultArt();

        OutputFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MixtapeCD");
        AlbumTitle.TextChanged += (_, _) => InvalidateBuild();
        Artist.TextChanged += (_, _) => InvalidateBuild();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Tracks.CollectionChanged += (_, _) => { Renumber(); SchedulePreview(); };

        if (FindFfmpeg() is null)
            StatusText.Text = "Note: ffmpeg not found. MP3 discs still work with MP3 files; for audio CDs or other formats put ffmpeg.exe next to MixtapeCD.exe.";
        SchedulePreview();
    }

    private static string? FindFfmpeg() => AudioPrep.FindFfmpeg();

    private bool Mp3Mode => DiscMode.SelectedItem is ComboBoxItem { Tag: true };

    private Mp3Quality SelectedQuality => Quality.SelectedItem is ComboBoxItem { Tag: Mp3Quality q } ? q : Mp3Quality.Best320;

    private void OnQualityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || DiscMode is null) return;
        // MP3 songs are unaffected; everything else is re-encoded at the new setting.
        foreach (Track track in Tracks.Where(t => t.Info is { IsMp3: false } && !t.Failed))
        {
            track.Mp3Path = null;
            _ = PrepareAsync(track);
        }
        InvalidateBuild();
        SchedulePreview();
    }

    private string Mp3Note(AudioInfo? info)
    {
        if (info is null) return "";
        string source = $"{info.FormatName}{(info.IsLossless ? " (lossless)" : "")}"
            + (info.BitrateKbps > 0 && !info.IsLossless ? $" {info.BitrateKbps} kbps" : "")
            + (info.SampleRate is not (44100 or 48000 or 32000) ? $" {info.SampleRate / 1000.0:0.#} kHz" : "")
            + (info.Channels == 1 ? " mono" : info.Channels > 2 ? $" {info.Channels}-ch" : "");
        if (info.IsMp3) return $"{source} · kept as-is, no quality loss";
        string target = SelectedQuality switch
        {
            Mp3Quality.VbrV0 => "MP3 V0",
            Mp3Quality.Cbr192 => "MP3 192 kbps",
            _ => "MP3 320 kbps",
        };
        return $"{source} → {target}";
    }

    private bool IsReady(Track t) => !t.Failed && (Mp3Mode ? t.Mp3Path is not null : t.WavPath is not null);

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || Gap is null) return;
        UpdateModeUi();
        SetDefaultArt(onlyIfBuiltIn: true);
        foreach (Track track in Tracks.Where(t => !t.Failed))
        {
            if (!IsReady(track)) _ = PrepareAsync(track);
            else track.Note = mp3Note(track);
        }
        InvalidateBuild();
        SchedulePreview();
    }

    private string mp3Note(Track track) => Mp3Mode ? Mp3Note(track.Info)
        : track.Info is { } info ? $"{info.FormatName} → CD audio" : "";

    private void UpdateModeUi()
    {
        bool mp3 = Mp3Mode;
        GapLabel.IsVisible = Gap.IsVisible = !mp3;
        QualityLabel.IsVisible = Quality.IsVisible = mp3;
        ModeHint.Text = mp3
            ? "Songs are stored as MP3 files in a thin band at the edge. Plays on computers and MP3-capable CD players (most car stereos)."
            : "Songs are normal CD tracks and play in any CD player, but they cover the middle of the disc. A last 'art' track plays as noise.";
    }

    /// <summary>Full-size art for MP3 discs, the ring-tiled version for audio CDs.</summary>
    private void SetDefaultArt(bool onlyIfBuiltIn = false)
    {
        string[] builtIn = BuiltInArtFiles().ToArray();
        if (onlyIfBuiltIn && _artSource is not null && !builtIn.Contains(_artSource)) return;
        string wanted = Mp3Mode ? "money.png" : "money-ring.png";
        if (builtIn.FirstOrDefault(a => Path.GetFileName(a) == wanted) is { } art) SetArt(art);
    }

    private static IEnumerable<string> BuiltInArtFiles()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "art");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.png").Order() : [];
    }

    // ---------- Songs ----------

    private async void OnAddSongs(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add songs",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = AudioPrep.Extensions.Select(x => "*" + x).ToArray() },
                FilePickerFileTypes.All,
            ],
        });
        AddSongs(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    internal void AddSongs(IEnumerable<string> paths)
    {
        foreach (string path in paths.Where(AudioPrep.IsAudio).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Tracks.Count >= 98) { StatusText.Text = "That's the maximum of 98 songs."; break; }
            Track track = new(path, CdMixtapeGenerator.SongTitle(path));
            Tracks.Add(track);
            _ = PrepareAsync(track);
        }
        InvalidateBuild();
    }

    private async Task PrepareAsync(Track track)
    {
        try
        {
            if (track.Info is null)
            {
                track.Status = "Reading…";
                track.Info = await Task.Run(() => AudioPrep.ProbeAsync(track.SourcePath, CancellationToken.None));
                if (track.Info is { Seconds: > 0 } probed) track.Seconds = probed.Seconds;
                // Prefer the song's own tags over its file name (unless the user already typed one).
                if (track.Info is { Title.Length: > 0 } tagged && track.Title == CdMixtapeGenerator.SongTitle(track.SourcePath))
                    track.Title = tagged.Title;
                if (track.Info is { Artist.Length: > 0 } withArtist && track.Artist.Length == 0)
                    track.Artist = withArtist.Artist;
            }
            if (Mp3Mode)
            {
                Mp3Quality quality = SelectedQuality;
                if (track.Info is not { IsMp3: true }) track.Status = "Converting to MP3…";
                string mp3 = await Task.Run(() => AudioPrep.PrepareMp3Async(track.SourcePath, track.Info, quality,
                    CancellationToken.None));
                if (quality != SelectedQuality) return; // setting changed meanwhile; a newer job takes over
                track.FileBytes = new FileInfo(mp3).Length;
                track.Mp3Path = mp3;
                track.Note = Mp3Note(track.Info);
            }
            else
            {
                track.Status = "Converting to CD audio…";
                string wav = await Task.Run(() => AudioPrep.PrepareAsync(track.SourcePath, CancellationToken.None));
                track.Sectors = CdMixtapeGenerator.SongSectors(wav, GapSeconds);
                track.WavPath = wav;
                track.Note = track.Info is { } info ? $"{info.FormatName} → CD audio" : "";
            }
            track.Status = "";
        }
        catch (Exception error)
        {
            track.Status = "Can't use this file: " + error.Message;
        }
        SchedulePreview();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(sender, -1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object? sender, int delta)
    {
        if ((sender as Control)?.Tag is not Track track) return;
        int index = Tracks.IndexOf(track), target = index + delta;
        if (index < 0 || target < 0 || target >= Tracks.Count) return;
        Tracks.Move(index, target);
        InvalidateBuild();
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Track track) Tracks.Remove(track);
        InvalidateBuild();
    }

    private void Renumber()
    {
        for (int i = 0; i < Tracks.Count; i++) Tracks[i].Number = i + 1;
        SongsHint.IsVisible = Tracks.Count == 0;
    }

    // ---------- Art & settings ----------

    private async void OnChooseArt(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose disc art",
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) SetArt(path);
    }

    private void OnBuiltInArt(object? sender, SelectionChangedEventArgs e)
    {
        if (BuiltInArt.SelectedItem is ComboBoxItem { Tag: string path }) SetArt(path);
    }

    internal void SetArt(string path)
    {
        _artSource = path;
        ArtName.Text = Path.GetFileName(path);
        PrepareArt();
    }

    private void OnArtFitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _artSource is not null) PrepareArt();
    }

    private void PrepareArt()
    {
        if (_artSource is null) return;
        try
        {
            bool fit = ArtFit.SelectedItem is ComboBoxItem { Tag: true };
            (string path, Bitmap square) = ArtPrep.PrepareSquare(_artSource, fit);
            _artPath = path;
            _artSquare = square;
            ArtThumb.Source = square;
        }
        catch (Exception error)
        {
            _artPath = null;
            _artSquare = null;
            ArtThumb.Source = null;
            SourceView.Source = null;
            StatusText.Text = $"Can't read that image ({error.Message}). Try a PNG or JPEG.";
        }
        InvalidateBuild();
        SchedulePreview();
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (sender == Gap)
            foreach (Track track in Tracks.Where(t => t.WavPath is not null))
                track.Sectors = CdMixtapeGenerator.SongSectors(track.WavPath!, GapSeconds);
        InvalidateBuild();
        SchedulePreview();
    }

    private double GapSeconds => Gap.SelectedItem is ComboBoxItem { Tag: double s } ? s : 0;
    private string DiscId => DiscType.SelectedItem is ComboBoxItem { Tag: string id } ? id : "verbatim-azo";

    // ---------- Drag & drop ----------

#pragma warning disable CS0618 // classic drag-drop API; works on all Avalonia 11 platforms
    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        string[] paths = (e.Data.GetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : [p]).ToArray();
        string? image = paths.FirstOrDefault(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        if (image is not null) SetArt(image);
        AddSongs(paths);
    }

#pragma warning restore CS0618

    // ---------- Preview ----------

    /// <summary>Re-render shortly after the last change (several songs finishing at once = one render).</summary>
    private async void SchedulePreview()
    {
        _debounce?.Cancel();
        CancellationTokenSource debounce = _debounce = new();
        try { await Task.Delay(250, debounce.Token); }
        catch (OperationCanceledException) { return; }
        await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        _previewCancel?.Cancel();
        CancellationTokenSource cancel = _previewCancel = new();
        CdDiscParameters disc = CdMixtapeGenerator.DiscParameters(DiscId);
        bool mp3 = Mp3Mode;
        bool preparing = Tracks.Any(t => !IsReady(t) && !t.Failed);
        long drawStart, drawEnd; // sectors that carry the picture
        if (mp3)
        {
            long fileSectors = Tracks.Where(IsReady).Sum(t => (t.FileBytes + 2047) / 2048);
            drawStart = 0;
            drawEnd = disc.OutputSectors - fileSectors;
            UpdateMp3LayoutText(disc, fileSectors, preparing);
        }
        else
        {
            long music = Tracks.Where(IsReady).Sum(t => t.Sectors);
            drawStart = Math.Min(music, disc.OutputSectors);
            drawEnd = disc.OutputSectors;
            UpdateLayoutText(disc, music, preparing);
        }
        if (_artPath is null || _artSquare is null) return;

        // Left: the user's image with the parts that won't be on the disc dimmed.
        double outer = disc.SamplingImageOuterRadiusMm;
        SourceView.Source = ArtPrep.RenderSourceView(_artSquare, 640,
            disc.InnerRadiusMm / outer,
            disc.RadiusFromByte(drawStart * CdDiscParameters.BytesPerSector) / outer,
            disc.RadiusFromByte(Math.Max(drawStart, drawEnd) * CdDiscParameters.BytesPerSector) / outer);

        PreviewBusy.IsVisible = true;
        try
        {
            string art = _artPath;
            byte[] pixels = await Task.Run(() => CdMp3DiscGenerator.RenderDiscPreview(
                art, disc, Tone, drawStart, Math.Max(drawStart, drawEnd), PreviewSize, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            WriteableBitmap bitmap = new(new PixelSize(PreviewSize, PreviewSize), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (ILockedFramebuffer frame = bitmap.Lock())
            {
                for (int y = 0; y < PreviewSize; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * PreviewSize * 4,
                        frame.Address + y * frame.RowBytes, PreviewSize * 4);
            }
            Preview.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Preview failed: " + error.Message;
        }
        finally
        {
            if (_previewCancel == cancel) PreviewBusy.IsVisible = false;
        }
    }

    private void UpdateLayoutText(CdDiscParameters disc, long music, bool preparing)
    {
        long capacity = disc.OutputSectors;
        long art = capacity - music;
        double musicFraction = Math.Clamp((double)music / capacity, 0, 1);
        MusicBar.Background = Avalonia.Media.Brush.Parse("#4a5a9e");
        ArtBar.Background = Avalonia.Media.Brush.Parse("#c9cfe6");
        CapacityBar.ColumnDefinitions[0].Width = new GridLength(musicFraction, GridUnitType.Star);
        CapacityBar.ColumnDefinitions[1].Width = new GridLength(1 - musicFraction, GridUnitType.Star);

        static string Time(long sectors)
        {
            long seconds = Math.Abs(sectors) / 75;
            return $"{seconds / 60}:{seconds % 60:00}";
        }
        TotalText.Text = Tracks.Count == 0 ? "" : $"{Tracks.Count} songs · {Time(music)} of {Time(capacity)} on this disc"
            + (preparing ? " (still converting…)" : "");

        if (art < CdMixtapeGenerator.MinimumTrackSectors)
        {
            PreviewOverlay.Text = $"Too much music: remove about {Time(CdMixtapeGenerator.MinimumTrackSectors - art)} to leave room for the art.";
            PreviewOverlay.IsVisible = true;
            LayoutText.Text = "No room for disc art.";
            BuildButton.IsEnabled = false;
            return;
        }

        PreviewOverlay.IsVisible = false;
        double from = CdMixtapeGenerator.PictureStartRadiusMm(disc, music);
        double ringWidth = disc.OutputOuterRadiusMm - from;
        string verdict = ringWidth >= 10 ? "plenty of room for the art"
            : ringWidth >= 5 ? "art is a medium ring" : "art is a thin band at the edge";
        LayoutText.Text = $"Music {Time(music)}  ·  Art track {Time(art)}  ·  Art ring {from:F1}–{disc.OutputOuterRadiusMm:F1} mm ({ringWidth:F1} mm wide, {verdict})";
        BuildButton.IsEnabled = CanBuild;
    }

    private bool CanBuild => !_building && Tracks.Count > 0 && Tracks.All(IsReady) && _artPath is not null;

    private void UpdateMp3LayoutText(CdDiscParameters disc, long fileSectors, bool preparing)
    {
        long capacity = disc.OutputSectors;
        double fraction = Math.Clamp((double)fileSectors / capacity, 0, 1);
        // Songs sit at the edge in MP3 mode: show them on the right of the bar.
        CapacityBar.ColumnDefinitions[0].Width = new GridLength(1 - fraction, GridUnitType.Star);
        CapacityBar.ColumnDefinitions[1].Width = new GridLength(fraction, GridUnitType.Star);
        MusicBar.Background = Avalonia.Media.Brush.Parse("#c9cfe6");
        ArtBar.Background = Avalonia.Media.Brush.Parse("#4a5a9e");

        double megabytes = Tracks.Where(IsReady).Sum(t => t.FileBytes) / 1048576.0;
        double seconds = Tracks.Sum(t => t.Seconds);
        string length = seconds > 0 ? $" · {(long)seconds / 60}:{(long)seconds % 60:00} of music" : "";
        TotalText.Text = Tracks.Count == 0 ? "" : $"{Tracks.Count} songs · {megabytes:F0} MB{length}"
            + (preparing ? " (still reading…)" : "");

        if (fileSectors > capacity - 30_000)
        {
            PreviewOverlay.Text = "Too many songs for an MP3 disc with art. Remove some songs.";
            PreviewOverlay.IsVisible = true;
            LayoutText.Text = "No room for disc art.";
            BuildButton.IsEnabled = false;
            return;
        }
        PreviewOverlay.IsVisible = false;
        double filesStart = CdMp3DiscGenerator.FilesStartRadiusMm(disc, fileSectors);
        double band = disc.OutputOuterRadiusMm - filesStart;
        LayoutText.Text = fileSectors == 0
            ? $"Art covers the whole disc ({disc.InnerRadiusMm:F1}–{disc.OutputOuterRadiusMm:F1} mm). Add songs to see where they go."
            : $"Art: {disc.InnerRadiusMm:F1}–{filesStart:F1} mm ({100 * (1 - fraction):F0}% of the disc)  ·  Songs: {band:F1} mm band at the edge ({megabytes:F0} MB)";
        BuildButton.IsEnabled = CanBuild;
    }

    // ---------- Build & burn ----------

    private async void OnChooseOutput(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Where to save the disc files" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) OutputFolder.Text = path;
    }

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        if (!CanBuild || _artPath is null) return;
        bool mp3 = Mp3Mode;
        string title = string.IsNullOrWhiteSpace(AlbumTitle.Text) ? "Mixtape" : AlbumTitle.Text.Trim();
        string folder = Path.Combine(OutputFolder.Text ?? ".", SafeFolderName(title));
        CdMixtapeSong[] songs = Tracks.Select(t => new CdMixtapeSong(mp3 ? t.Mp3Path! : t.WavPath!,
            string.IsNullOrWhiteSpace(t.Title) ? $"Track {t.Number}" : t.Title.Trim())).ToArray();
        CdDiscParameters disc = CdMixtapeGenerator.DiscParameters(DiscId);
        string artist = Artist.Text?.Trim() ?? "";
        string art = _artPath;
        double gap = GapSeconds;

        _building = true;
        BuildButton.IsEnabled = BurnButton.IsEnabled = false;
        Progress.IsVisible = true;
        Progress.Value = 0;
        StatusText.Text = "Building the disc…";
        Action<CdProgress> report = p => Dispatcher.UIThread.Post(() => Progress.Value = p.Fraction);
        try
        {
            if (Directory.Exists(folder))
                foreach (string old in Directory.EnumerateFiles(folder)
                             .Where(f => f.EndsWith(".wav") || f.EndsWith(".wav.json") || f.EndsWith(".iso") || f.EndsWith(".iso.json")))
                    File.Delete(old);
            if (mp3)
            {
                StatusText.Text = "Writing song tags…";
                string tagged = Path.Combine(Path.GetTempPath(), "MixtapeCD", "tagged-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tagged);
                try
                {
                    // Tags need ffmpeg; without it the MP3s go on exactly as they are.
                    for (int i = 0; i < songs.Length && FindFfmpeg() is not null; i++)
                    {
                        string destination = Path.Combine(tagged, $"{i + 1:000}.mp3");
                        string songArtist = Tracks[i].Artist.Trim();
                        await AudioPrep.TagMp3Async(songs[i].WavPath, destination, songs[i].Title,
                            songArtist.Length > 0 ? songArtist : artist, artist, title, i + 1, songs.Length,
                            CancellationToken.None);
                        songs[i] = songs[i] with { WavPath = destination };
                    }
                    StatusText.Text = "Building the disc…";
                    CdMp3DiscSummary summary = await Task.Run(() => CdMp3DiscGenerator.Generate(
                        new CdMp3DiscRequest(art, songs, folder, disc, Tone, title, artist), report));
                    _builtCue = summary.IsoPath;
                    StatusText.Text = $"Ready to burn: MP3 disc with {summary.SongCount} songs. Saved as {summary.IsoPath}";
                }
                finally
                {
                    try { Directory.Delete(tagged, recursive: true); } catch (IOException) { }
                }
            }
            else
            {
                CdMixtapeSummary summary = await Task.Run(() => CdMixtapeGenerator.Generate(
                    new CdMixtapeRequest(art, "", folder, disc, Tone, GapSeconds: gap, AlbumTitle: title, Performer: artist)
                    { Songs = songs }, report));
                _builtCue = summary.CueSheetPath;
                StatusText.Text = $"Ready to burn: {summary.MusicTracks.Count} songs + art track. Saved in {folder}";
            }
            BurnButton.IsEnabled = OpenButton.IsEnabled = true;
        }
        catch (Exception error)
        {
            StatusText.Text = "Build failed: " + error.Message;
        }
        finally
        {
            _building = false;
            Progress.IsVisible = false;
            SchedulePreview();
        }
    }

    private void OnBurn(object? sender, RoutedEventArgs e)
    {
        if (_builtCue is null) return;
        string? imgBurn = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }.Where(d => d.Length > 0).Select(d => Path.Combine(d, "ImgBurn", "ImgBurn.exe")).FirstOrDefault(File.Exists);
        if (imgBurn is null)
        {
            StatusText.Text = "ImgBurn isn't installed. Get it free from imgburn.com, then click Burn again. "
                + "(Or burn the file in the output folder with any burner's 'burn image' option.)";
            OpenUrl("https://www.imgburn.com/index.php?act=download");
            return;
        }
        // ImgBurn opens in Write mode with the disc loaded; the user checks speed and presses Write.
        ProcessStartInfo start = new(imgBurn) { UseShellExecute = false };
        start.ArgumentList.Add("/MODE");
        start.ArgumentList.Add("WRITE");
        start.ArgumentList.Add("/SRC");
        start.ArgumentList.Add(_builtCue);
        Process.Start(start);
        StatusText.Text = "ImgBurn opened with your disc. Insert a blank Verbatim CD-R, pick a slow write speed, and click Write.";
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_builtCue is not null) OpenUrl(Path.GetDirectoryName(_builtCue)!);
    }

    private void InvalidateBuild()
    {
        _builtCue = null;
        if (BurnButton is null) return;
        BurnButton.IsEnabled = OpenButton.IsEnabled = false;
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private static string SafeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return cleaned.Length == 0 ? "Mixtape" : cleaned;
    }
}
