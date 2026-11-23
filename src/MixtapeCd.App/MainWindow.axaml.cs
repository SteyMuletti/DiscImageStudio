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
    private string? _artPath;
    private string? _builtCue; // .cue (audio CD) or .iso (MP3 disc) ready for ImgBurn
    private bool _building;

    public ObservableCollection<Track> Tracks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        DiscMode.Items.Add(new ComboBoxItem { Content = "MP3 disc: art covers the whole disc", Tag = true });
        DiscMode.Items.Add(new ComboBoxItem { Content = "Audio CD: art on the outer ring only", Tag = false });
        DiscMode.SelectedIndex = 0;
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

    private bool IsReady(Track t) => !t.Failed && (Mp3Mode ? t.Mp3Path is not null : t.WavPath is not null);

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || Gap is null) return;
        UpdateModeUi();
        SetDefaultArt(onlyIfBuiltIn: true);
        foreach (Track track in Tracks.Where(t => !IsReady(t) && !t.Failed)) _ = PrepareAsync(track);
        InvalidateBuild();
        SchedulePreview();
    }

    private void UpdateModeUi()
    {
        bool mp3 = Mp3Mode;
        GapLabel.IsVisible = Gap.IsVisible = !mp3;
        ModeHint.Text = mp3
            ? "Songs are stored as MP3 files in a thin band at the edge. Plays on computers and MP3-capable CD players (most car stereos)."
            : "Songs are normal CD tracks and play in any CD player, but they cover the middle of the disc. A last 'art' track plays as noise.";
    }

    /// <summary>Full-size art for MP3 discs, the ring-tiled version for audio CDs.</summary>
    private void SetDefaultArt(bool onlyIfBuiltIn = false)
    {
        string[] builtIn = BuiltInArtFiles().ToArray();
        if (onlyIfBuiltIn && _artPath is not null && !builtIn.Contains(_artPath)) return;
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
            if (Mp3Mode)
            {
                bool convert = !Path.GetExtension(track.SourcePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
                track.Status = convert ? "Converting to MP3…" : "Reading…";
                string mp3 = await Task.Run(() => AudioPrep.PrepareMp3Async(track.SourcePath, CancellationToken.None));
                track.FileBytes = new FileInfo(mp3).Length;
                if (track.Seconds == 0)
                    track.Seconds = await Task.Run(() => AudioPrep.ProbeSecondsAsync(mp3, CancellationToken.None));
                track.Mp3Path = mp3;
            }
            else
            {
                track.Status = "Converting to CD audio…";
                string wav = await Task.Run(() => AudioPrep.PrepareAsync(track.SourcePath, CancellationToken.None));
                track.Sectors = CdMixtapeGenerator.SongSectors(wav, GapSeconds);
                track.WavPath = wav;
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
        try
        {
            using FileStream stream = File.OpenRead(path);
            ArtThumb.Source = Bitmap.DecodeToWidth(stream, 176);
        }
        catch (Exception)
        {
            ArtThumb.Source = null; // the engine may still read formats the thumbnail can't
        }
        _artPath = path;
        ArtName.Text = Path.GetFileName(path);
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
        if (_artPath is null) return;

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
                CdMp3DiscSummary summary = await Task.Run(() => CdMp3DiscGenerator.Generate(
                    new CdMp3DiscRequest(art, songs, folder, disc, Tone, title, artist), report));
                _builtCue = summary.IsoPath;
                StatusText.Text = $"Ready to burn: MP3 disc with {summary.SongCount} songs. Saved as {summary.IsoPath}";
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
