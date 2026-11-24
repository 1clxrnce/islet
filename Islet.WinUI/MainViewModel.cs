using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Color = Windows.UI.Color;

namespace Islet;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Color FallbackAccent = MediaService.FallbackAccent;
    private const int ArtDecodeWidth = 256; // Largest size the panel shows, at 2x DPI

    // Segoe Fluent Icons / MDL2 glyphs.
    private const string GlyphMute = "";
    private const string GlyphVolume0 = "";
    private const string GlyphVolume1 = "";
    private const string GlyphVolume2 = "";
    private const string GlyphVolume3 = "";

    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();

    private string _title = "";
    private string _artist = "";
    private ImageSource? _albumArt;
    private string? _artHash;
    private Color _accent = FallbackAccent;
    private bool _isPlaying;
    private bool _isActive;
    private bool _isLive;
    private bool _canSeek;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private TimeSpan _positionBase;
    private TimeSpan _displayPosition;
    private double _rate = 1;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;
    private int _trackRevision;
    private string _sourceAppId = "";
    private double? _appVolume;
    private IReadOnlyList<AudioDevice> _devices = Array.Empty<AudioDevice>();
    private bool _disposed;

    public MainViewModel()
    {
        _media.PreferredSessionId = AppSettings.Current.PreferredSessionId;
        _media.StateChanged += OnMediaStateChanged;
        _volume.Changed += () => RefreshVolume(); // Raised by Windows, already on the UI thread
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

    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value)) OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string Artist { get => _artist; private set => Set(ref _artist, value); }

    public const string NothingPlaying = "No song playing";

    // Never blank: with no session (or no title yet) the pill and panel say so.
    public string DisplayTitle => !_isActive || string.IsNullOrWhiteSpace(_title) ? NothingPlaying : _title;

    public ImageSource? AlbumArt { get => _albumArt; private set => Set(ref _albumArt, value); }
    public Color AccentColor => _accent;
    // Animated by the window, so it is a single live brush rather than a new one per change.
    public SolidColorBrush AccentBrush { get; } = new(FallbackAccent);
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (Set(ref _isActive, value)) OnPropertyChanged(nameof(DisplayTitle));
        }
    }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public bool CanSeek { get => _canSeek; private set => Set(ref _canSeek, value); }

    // Enabled whenever a session is active (playing or paused), disabled with nothing playing.
    // The app's own flags are not trusted: many report a control as disabled yet accept it,
    // and MediaService falls back to media keys.
    public bool CanPlayPause => _isActive;
    public bool CanNext => _isActive;
    public bool CanPrevious => _isActive;
    public int TrackRevision => _trackRevision;

    public TimeSpan Position => _displayPosition;
    public TimeSpan Duration => _endTime > _startTime ? _endTime - _startTime : TimeSpan.Zero;
    public double Progress => !IsLive && Duration > TimeSpan.Zero
        ? Math.Clamp(_displayPosition.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;
    public string Elapsed => FormatTime(_displayPosition);
    public string Remaining => IsLive ? "LIVE" : "-" + FormatTime(Duration - _displayPosition);
    public double PlaybackRate => _rate;

    // Master volume in percent; two-way bound to the slider.
    public double Volume
    {
        get => _volume.Volume * 100;
        set => SetVolume(value);
    }

    public bool IsMuted => _volume.IsMuted;

    // Volume of the app that is playing, in percent. Hidden when Windows has no session for it.
    public bool HasAppVolume => _appVolume.HasValue;
    public string AppName => MediaService.FriendlyName(_sourceAppId);
    public double AppVolume
    {
        get => (_appVolume ?? 0) * 100;
        set
        {
            if (!HasAppVolume) return;
            _volume.SetAppVolume(_sourceAppId, value / 100.0);
            _appVolume = Math.Clamp(value / 100.0, 0, 1);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<AudioDevice> Devices => _devices;

    public string? DefaultDeviceId
    {
        get => _volume.DefaultDeviceId;
        set
        {
            // A combo box pushes null while its list is being replaced; ignore that.
            if (string.IsNullOrEmpty(value) || value == _volume.DefaultDeviceId) return;
            _volume.SetDefaultDevice(value);
            OnPropertyChanged();
            RefreshVolume();
        }
    }

    public string VolumeGlyph => IsMuted ? GlyphMute : Volume switch
    {
        <= 0 => GlyphVolume0,
        < 34 => GlyphVolume1,
        < 67 => GlyphVolume2,
        _ => GlyphVolume3
    };

    public IReadOnlyList<MediaSessionInfo> Sessions => _media.GetSessionInfos();

    public string? PreferredSessionId
    {
        get => _media.PreferredSessionId;
        set
        {
            if (_media.PreferredSessionId == value) return;
            _media.PreferredSessionId = value;
            AppSettings.Current.PreferredSessionId = value;
            AppSettings.Save();
            _media.Refresh();
        }
    }

    public async Task InitializeAsync()
    {
        try { await _media.StartAsync(); }
        catch (Exception ex) { Log.Error(ex, "Media access unavailable"); }
    }

    public void SyncTimeline() => _media.RefreshTimeline();

    public void RefreshVolume()
    {
        double before = Volume;
        bool wasMuted = IsMuted;
        string glyph = VolumeGlyph;
        string? device = DefaultDeviceId;
        _volume.Refresh();
        if (Volume != before) OnPropertyChanged(nameof(Volume));
        if (IsMuted != wasMuted) OnPropertyChanged(nameof(IsMuted));
        if (VolumeGlyph != glyph) OnPropertyChanged(nameof(VolumeGlyph));
        if (DefaultDeviceId != device) OnPropertyChanged(nameof(DefaultDeviceId));
    }

    // Not polled: called when the source app changes and while the panel is open.
    public void RefreshAppVolume(bool findSessionsAgain = false)
    {
        if (findSessionsAgain) _volume.InvalidateAppSessions();
        bool had = HasAppVolume;
        double? next = _volume.GetAppVolume(_sourceAppId);
        if (next == _appVolume) return;
        _appVolume = next;
        OnPropertyChanged(nameof(AppVolume));
        if (HasAppVolume != had) OnPropertyChanged(nameof(HasAppVolume));
    }

    // Called when the device list is opened, so it is never stale and never polled.
    public void RefreshDevices()
    {
        _devices = _volume.GetDevices();
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(DefaultDeviceId));
    }

    public void SetVolume(double percent)
    {
        bool wasMuted = IsMuted;
        _volume.SetVolume(percent / 100.0);
        OnPropertyChanged(nameof(Volume));
        if (IsMuted != wasMuted) OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    public void ChangeVolume(double deltaPercent) => SetVolume(Volume + deltaPercent);

    public void ToggleMute()
    {
        _volume.SetMute(!_volume.IsMuted);
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    // relativePosition is measured from the start of the track (0 .. Duration).
    public Task SeekAsync(TimeSpan relativePosition)
    {
        if (IsLive || !CanSeek || Duration <= TimeSpan.Zero) return Task.CompletedTask;
        var target = TimeSpan.FromTicks(Math.Clamp(relativePosition.Ticks, 0, Duration.Ticks));
        _displayPosition = target;
        _positionBase = target;
        _lastUpdated = DateTimeOffset.UtcNow;
        RaisePositionChanged();
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
        RaisePositionChanged();
    }

    private void OnMediaStateChanged(object? sender, MediaSnapshot snapshot)
    {
        // WinRT raises this on worker threads; marshal to the UI without blocking the caller.
        Ui.Post(() => Apply(snapshot));
    }

    private void Apply(MediaSnapshot snapshot)
    {
        if (_disposed) return;

        // With nothing playing, nothing stale is shown: no old title, artist, art or progress.
        string title = snapshot.IsActive ? snapshot.Title : "";
        if (title != _title)
        {
            _trackRevision++;
            Title = title;
            OnPropertyChanged(nameof(TrackRevision));
        }
        Artist = snapshot.IsActive ? snapshot.Artist : "";
        UpdateArt(snapshot.IsActive ? snapshot.Art : null, snapshot.IsActive ? snapshot.ArtHash : null);
        if (snapshot.Accent != _accent)
        {
            _accent = snapshot.Accent;
            OnPropertyChanged(nameof(AccentColor));
        }

        IsPlaying = snapshot.IsPlaying;
        IsActive = snapshot.IsActive;
        IsLive = snapshot.IsLive;
        CanSeek = snapshot.CanSeek;
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

        if (snapshot.SourceAppId != _sourceAppId)
        {
            _sourceAppId = snapshot.SourceAppId;
            OnPropertyChanged(nameof(AppName));
            RefreshAppVolume();
        }

        OnPropertyChanged(nameof(Duration));
        RaisePositionChanged();
    }

    // Decoded once per image, at display size (not full resolution), on the UI thread as WinUI requires.
    private void UpdateArt(byte[]? bytes, string? hash)
    {
        if (hash == _artHash) return;
        _artHash = hash;
        if (bytes is null)
        {
            AlbumArt = null;
            return;
        }

        var bitmap = new BitmapImage { DecodePixelWidth = ArtDecodeWidth, DecodePixelType = DecodePixelType.Physical };
        AlbumArt = bitmap;
        _ = LoadArtAsync(bitmap, bytes);
    }

    private static async Task LoadArtAsync(BitmapImage bitmap, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Decoding album art failed: {ex.Message}");
        }
    }

    private void RaisePositionChanged()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(Remaining));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _media.StateChanged -= OnMediaStateChanged;
        _media.Dispose();
        _volume.Dispose();
    }
}
