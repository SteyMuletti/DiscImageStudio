using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MixtapeCd.App;

/// <summary>One song in the running order.</summary>
public sealed class Track(string sourcePath, string title) : INotifyPropertyChanged
{
    private int _number;
    private string _title = title;
    private string? _wavPath;
    private long _sectors;
    private string _status = "Preparing…";
    private string? _mp3Path;
    private long _fileBytes;
    private double _seconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourcePath { get; } = sourcePath;

    public int Number { get => _number; set { _number = value; Raise(); Raise(nameof(NumberText)); } }
    public string NumberText => $"{Number:00}";

    public string Title { get => _title; set { _title = value; Raise(); } }

    /// <summary>CD-ready WAV once conversion finished; null while preparing or after a failure.</summary>
    public string? WavPath { get => _wavPath; set { _wavPath = value; Raise(); } }

    /// <summary>On-disc length including padding and gap.</summary>
    public long Sectors { get => _sectors; set { _sectors = value; Raise(); Raise(nameof(DurationText)); } }
    public string DurationText
    {
        get
        {
            double seconds = Seconds > 0 ? Seconds : Sectors / 75.0;
            if (seconds <= 0) return "–";
            long whole = (long)Math.Round(seconds);
            return $"{whole / 60}:{whole % 60:00}";
        }
    }

    /// <summary>The file that goes on an MP3 disc (the original MP3, or a converted copy).</summary>
    public string? Mp3Path { get => _mp3Path; set { _mp3Path = value; Raise(); } }
    public long FileBytes { get => _fileBytes; set { _fileBytes = value; Raise(); } }
    public double Seconds { get => _seconds; set { _seconds = value; Raise(); Raise(nameof(DurationText)); } }

    public string Status { get => _status; set { _status = value; Raise(); Raise(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public bool Failed => Status.StartsWith("Can't", StringComparison.Ordinal);

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
