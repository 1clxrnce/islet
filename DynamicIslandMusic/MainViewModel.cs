using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace DynamicIslandMusic;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    // Settings the options window edits. Kept in memory for this session.
    public static bool AutoHide = true;
    public static bool DodgeOnHover = true;
    public static bool HotkeysEnabled = true;
    public static int MonitorIndex = -1;

    private readonly MediaService _media;
    private readonly VolumeService _volume = new();

    private string _title = "";
    private string _artist = "";
    private ImageSource? _albumArt;
    private Color _accent = Color.FromArgb(255, 110, 110, 120);
    private bool _isPlaying;
    private bool _isActive;
    private bool _isLive;
    private bool _canSeek;
    private bool _canPlay;
    private bool _canPause;
    private bool _canNext;
    private bool _canPrevious;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private TimeSpan _positionBase;
    private TimeSpan _displayPosition;
    private double _rate = 1;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;
    private int _trackRevision;

    public MainViewModel()
    {
        _media = new MediaService();
        _media.StateChanged += OnMediaStateChanged;
        _volume.Attach();
        PlayPauseCommand = new RelayCommand(() => _ = _media.PlayPauseAsync());
        PreviousCommand = new RelayCommand(() => _ = _media.PreviousAsync());
        NextCommand = new RelayCommand(() => _ = _media.NextAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PlayPauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public MediaService Media => _media;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public ImageSource? AlbumArt { get => _albumArt; private set => Set(ref _albumArt, value); }
    public Color AccentColor { get => _accent; private set => Set(ref _accent, value); }
    public SolidColorBrush AccentBrush { get; } = new(Color.FromArgb(255, 110, 110, 120));
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public bool CanSeek { get => _canSeek; private set => Set(ref _canSeek, value); }
    public bool CanPlay { get => _canPlay; private set => Set(ref _canPlay, value); }
    public bool CanPause { get => _canPause; private set => Set(ref _canPause, value); }
    // Stay enabled whenever a session exists; MediaService falls back to media keys.
    public bool CanPlayPause => IsActive || CanPlay || CanPause;
    public bool CanNext { get => IsActive || _canNext; private set => Set(ref _canNext, value); }
    public bool CanPrevious { get => IsActive || _canPrevious; private set => Set(ref _canPrevious, value); }
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);
    public int TrackRevision => _trackRevision;

    public TimeSpan Position => _displayPosition;
    public TimeSpan Duration => _endTime > _startTime ? _endTime - _startTime : TimeSpan.Zero;
    public double Progress => !IsLive && Duration > TimeSpan.Zero
        ? Math.Clamp(_displayPosition.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;
    public string Elapsed => Format(_displayPosition);
    public string Total => IsLive ? "LIVE" : Format(Duration);

    public double Volume => _volume.Volume * 100;
    public bool IsMuted => _volume.IsMuted;
    public string VolumeGlyph => IsMuted || Volume <= 0 ? "\uE771" : "\uE767";

    public IReadOnlyList<MediaSessionInfo> Sessions => _media.GetSessionInfos();
    public string? PreferredSessionId
    {
        get => _media.PreferredSessionId;
        set
        {
            _media.PreferredSessionId = value;
            _media.Refresh();
        }
    }

    public async Task InitializeAsync()
    {
        try { await _media.StartAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Media access unavailable: {ex.Message}"); }
    }

    public void SyncTimeline() => _media.RefreshTimeline();

    public void RefreshVolume()
    {
        double before = Volume;
        bool wasMuted = IsMuted;
        _volume.Refresh();
        if (Volume != before) OnPropertyChanged(nameof(Volume));
        if (IsMuted != wasMuted)
        {
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public void SetVolume(double percent)
    {
        _volume.SetVolume(percent / 100.0);
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    public void ChangeVolume(double deltaPercent) => SetVolume(Volume + deltaPercent);

    public void ToggleMute()
    {
        _volume.SetMute(!_volume.IsMuted);
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    public Task SeekAsync(TimeSpan relativePosition)
    {
        if (IsLive || !CanSeek || Duration <= TimeSpan.Zero) return Task.CompletedTask;
        var target = TimeSpan.FromTicks(Math.Clamp(relativePosition.Ticks, 0, Duration.Ticks));
        _displayPosition = target;
        _positionBase = target;
        _lastUpdated = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Elapsed));
        return _media.SeekAsync(_startTime + target);
    }

    // Position is a snapshot at LastUpdatedTime; advance it locally using the real PlaybackRate.
    public void Tick()
    {
        if (!_isPlaying || IsLive || _lastUpdated == DateTimeOffset.MinValue) return;

        var age = DateTimeOffset.UtcNow - _lastUpdated;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        var next = _positionBase + TimeSpan.FromTicks((long)(age.Ticks * _rate));
        if (Duration > TimeSpan.Zero && next > Duration) next = Duration;
        if (next == _displayPosition) return;

        _displayPosition = next;
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Elapsed));
    }

    private void OnMediaStateChanged(object? sender, MediaSnapshot snapshot)
    {
        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Title) && snapshot.Title != _title)
            {
                _trackRevision++;
                Title = snapshot.Title;
                Artist = snapshot.Artist;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.Artist))
            {
                Artist = snapshot.Artist;
            }

            AlbumArt = snapshot.Art;
            if (snapshot.Accent != _accent)
            {
                _accent = snapshot.Accent;
                OnPropertyChanged(nameof(AccentColor));
            }

            IsPlaying = snapshot.IsPlaying;
            IsActive = snapshot.IsActive;
            IsLive = snapshot.IsLive;
            CanSeek = snapshot.CanSeek;
            CanPlay = snapshot.CanPlay;
            CanPause = snapshot.CanPause;
            CanNext = snapshot.CanNext;
            CanPrevious = snapshot.CanPrevious;
            OnPropertyChanged(nameof(CanPlayPause));
            OnPropertyChanged(nameof(CanNext));
            OnPropertyChanged(nameof(CanPrevious));
            _startTime = snapshot.StartTime;
            _endTime = snapshot.EndTime;
            _rate = snapshot.PlaybackRate > 0 ? snapshot.PlaybackRate : 1;
            _lastUpdated = snapshot.LastUpdated;
            _positionBase = snapshot.Position >= snapshot.StartTime
                ? snapshot.Position - snapshot.StartTime
                : snapshot.Position;
            _displayPosition = Duration > TimeSpan.Zero && _positionBase > Duration
                ? Duration
                : _positionBase;

            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(Elapsed));
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(TrackRevision));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.Invoke(Apply);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public ValueTask DisposeAsync()
    {
        _media.StateChanged -= OnMediaStateChanged;
        _media.Dispose();
        return ValueTask.CompletedTask;
    }
}
