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

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourcePath { get; } = sourcePath;

    public int Number { get => _number; set { _number = value; Raise(); Raise(nameof(NumberText)); } }
    public string NumberText => $"{Number:00}";

    public string Title { get => _title; set { _title = value; Raise(); } }

    /// <summary>CD-ready WAV once conversion finished; null while preparing or after a failure.</summary>
    public string? WavPath { get => _wavPath; set { _wavPath = value; Raise(); } }

    /// <summary>On-disc length including padding and gap.</summary>
    public long Sectors { get => _sectors; set { _sectors = value; Raise(); Raise(nameof(DurationText)); } }
    public string DurationText => Sectors == 0 ? "–" : TimeSpan.FromSeconds(Sectors / 75.0).ToString(@"m\:ss");

    public string Status { get => _status; set { _status = value; Raise(); Raise(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public bool Ready => WavPath is not null && Status.Length == 0;

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
