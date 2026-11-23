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
    private string? _builtCue;
    private bool _building;

    public ObservableCollection<Track> Tracks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        foreach ((string id, string name) in CdMixtapeGenerator.DiscTypes)
            DiscType.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        DiscType.SelectedIndex = 0;
        foreach ((string label, double seconds) in new[] { ("No gap (seamless)", 0.0), ("1 second", 1), ("2 seconds", 2) })
            Gap.Items.Add(new ComboBoxItem { Content = label, Tag = seconds });
        Gap.SelectedIndex = 0;

        foreach (string art in BuiltInArtFiles())
            BuiltInArt.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(art), Tag = art });
        if (BuiltInArtFiles().FirstOrDefault(a => a.Contains("money-ring")) is { } defaultArt)
            SetArt(defaultArt);

        OutputFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MixtapeCD");
        AlbumTitle.TextChanged += (_, _) => InvalidateBuild();
        Artist.TextChanged += (_, _) => InvalidateBuild();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Tracks.CollectionChanged += (_, _) => { Renumber(); SchedulePreview(); };

        if (FindFfmpeg() is null)
            StatusText.Text = "Note: ffmpeg not found. CD-format WAV files work; for MP3/FLAC/M4A put ffmpeg.exe next to MixtapeCD.exe.";
        SchedulePreview();
    }

    private static string? FindFfmpeg() => AudioPrep.FindFfmpeg();

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
            if (Tracks.Count >= 98) { StatusText.Text = "A CD holds at most 98 songs plus the art track."; break; }
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
            track.Status = "Converting to CD audio…";
            string wav = await Task.Run(() => AudioPrep.PrepareAsync(track.SourcePath, CancellationToken.None));
            track.WavPath = wav;
            track.Sectors = CdMixtapeGenerator.SongSectors(wav, GapSeconds);
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
        long music = Tracks.Sum(t => t.Sectors);
        bool preparing = Tracks.Any(t => t.WavPath is null && !t.Status.StartsWith("Can't", StringComparison.Ordinal));
        UpdateLayoutText(disc, music, preparing);
        if (_artPath is null) return;

        PreviewBusy.IsVisible = true;
        try
        {
            string art = _artPath;
            byte[] pixels = await Task.Run(() => CdMixtapeGenerator.RenderDiscPreview(
                art, disc, Tone, Math.Min(music, disc.OutputSectors), PreviewSize, cancel.Token), cancel.Token);
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
        BuildButton.IsEnabled = !_building && Tracks.Count > 0 && Tracks.All(t => t.Ready) && _artPath is not null;
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
        if (_artPath is null || Tracks.Count == 0 || !Tracks.All(t => t.Ready)) return;
        string title = string.IsNullOrWhiteSpace(AlbumTitle.Text) ? "Mixtape" : AlbumTitle.Text.Trim();
        string folder = Path.Combine(OutputFolder.Text ?? ".", SafeFolderName(title));
        CdMixtapeRequest request = new(_artPath, "", folder, CdMixtapeGenerator.DiscParameters(DiscId), Tone,
            GapSeconds: GapSeconds, AlbumTitle: title, Performer: Artist.Text?.Trim() ?? "")
        {
            Songs = Tracks.Select(t => new CdMixtapeSong(t.WavPath!, string.IsNullOrWhiteSpace(t.Title) ? $"Track {t.Number}" : t.Title.Trim())).ToArray(),
        };

        _building = true;
        BuildButton.IsEnabled = BurnButton.IsEnabled = false;
        Progress.IsVisible = true;
        Progress.Value = 0;
        StatusText.Text = "Building disc files…";
        try
        {
            if (Directory.Exists(folder))
                foreach (string old in Directory.EnumerateFiles(folder, "*.wav").Concat(Directory.EnumerateFiles(folder, "*.wav.json")))
                    File.Delete(old);
            CdMixtapeSummary summary = await Task.Run(() => CdMixtapeGenerator.Generate(request,
                p => Dispatcher.UIThread.Post(() => Progress.Value = p.Fraction)));
            _builtCue = summary.CueSheetPath;
            StatusText.Text = $"Ready to burn: {summary.MusicTracks.Count} songs + art track. Saved in {folder}";
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
                + "(Or burn mixtape.cue with any burner that does disc-at-once.)";
            OpenUrl("https://www.imgburn.com/index.php?act=download");
            return;
        }
        // ImgBurn opens in Write mode with the CUE loaded; the user checks speed and presses Write.
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
