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
    // Set false to keep the island parked in compact state instead of sliding away.
    public static readonly bool AutoHide = true;

    private readonly MediaService _media;

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
        PlayPauseCommand = new RelayCommand(() => _ = _media.PlayPauseAsync());
        PreviousCommand = new RelayCommand(() => _ = _media.PreviousAsync());
        NextCommand = new RelayCommand(() => _ = _media.NextAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PlayPauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

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
    public bool CanPlayPause => CanPlay || CanPause;
    public bool CanNext { get => _canNext; private set => Set(ref _canNext, value); }
    public bool CanPrevious { get => _canPrevious; private set => Set(ref _canPrevious, value); }
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);
    public int TrackRevision => _trackRevision;

    public TimeSpan Position => _displayPosition;
    public TimeSpan Duration => _endTime > _startTime ? _endTime - _startTime : TimeSpan.Zero;

    public double Progress => !IsLive && Duration > TimeSpan.Zero
        ? Math.Clamp(_displayPosition.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;

    public string Elapsed => Format(_displayPosition);
    public string Total => IsLive ? "LIVE" : Format(Duration);

    public Task InitializeAsync() => _media.StartAsync();

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
            OnPropertyChanged(nameof(CanPlayPause));
            CanNext = snapshot.CanNext;
            CanPrevious = snapshot.CanPrevious;
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
