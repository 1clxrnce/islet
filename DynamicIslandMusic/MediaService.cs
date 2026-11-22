using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Color = System.Windows.Media.Color;

namespace DynamicIslandMusic;

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    ImageSource? Art,
    Color Accent,
    bool IsPlaying,
    bool IsActive,
    bool IsLive,
    bool CanSeek,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    TimeSpan Position,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    double PlaybackRate,
    DateTimeOffset LastUpdated);

public sealed class MediaService : IDisposable
{
    private static readonly Color FallbackAccent = Color.FromArgb(255, 110, 110, 120);

    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _lastPlaying;
    private string? _artKey;
    private ImageSource? _cachedArt;
    private Color _cachedAccent = FallbackAccent;
    private long _version;
    private bool _disposed;

    public event EventHandler<MediaSnapshot>? StateChanged;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnManagerChanged;
        _manager.SessionsChanged += OnManagerChanged;
        SubscribeAll();
        SelectSession();
        await PublishAsync();
    }

    private void SubscribeAll()
    {
        if (_manager is null) return;

        foreach (var old in _subscribed)
            old.PlaybackInfoChanged -= OnAnySessionChanged;
        _subscribed.Clear();

        foreach (var session in _manager.GetSessions())
        {
            session.PlaybackInfoChanged += OnAnySessionChanged;
            _subscribed.Add(session);
        }
    }

    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        SubscribeAll();
        SelectSession();
        _ = PublishAsync();
    }

    private void OnAnySessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        SelectSession();
        _ = PublishAsync();
    }

    // Prefer the session actually playing, then the last one that played, then the manager's pick.
    private void SelectSession()
    {
        if (_manager is null) return;

        var sessions = _manager.GetSessions();
        var playing = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (playing is not null) _lastPlaying = playing;

        var next = playing
                   ?? (_lastPlaying is not null && sessions.Contains(_lastPlaying) ? _lastPlaying : null)
                   ?? _manager.GetCurrentSession();
        Attach(next);
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }

        _session = session;
        _artKey = null;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnSessionChanged;
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _ = PublishAsync();

    private async Task PublishAsync()
    {
        if (_disposed) return;
        long version = Interlocked.Increment(ref _version);

        string title = "";
        string artist = "";
        var accent = FallbackAccent;
        var position = TimeSpan.Zero;
        var start = TimeSpan.Zero;
        var end = TimeSpan.Zero;
        var minSeek = TimeSpan.Zero;
        var maxSeek = TimeSpan.Zero;
        var lastUpdated = DateTimeOffset.MinValue;
        double rate = 1;
        bool isPlaying = false;
        bool isLive = false;
        bool canSeek = false;
        bool canPlay = false;
        bool canPause = false;
        bool canNext = false;
        bool canPrevious = false;
        bool active = false;

        var session = _session;
        if (session is not null)
        {
            var info = session.GetPlaybackInfo();
            var status = info?.PlaybackStatus
                         ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            var controls = info?.Controls;

            // Visibility and enabled state come from the session, never from transient metadata.
            active = status is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                                   or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            canPlay = controls?.IsPlayEnabled ?? false;
            canPause = controls?.IsPauseEnabled ?? false;
            canNext = controls?.IsNextEnabled ?? false;
            canPrevious = controls?.IsPreviousEnabled ?? false;
            canSeek = controls?.IsPlaybackPositionEnabled ?? false;
            rate = info?.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1;

            var timeline = session.GetTimelineProperties();
            if (timeline is not null)
            {
                start = timeline.StartTime;
                end = timeline.EndTime;
                position = timeline.Position;
                minSeek = timeline.MinSeekTime;
                maxSeek = timeline.MaxSeekTime;
                lastUpdated = timeline.LastUpdatedTime == DateTimeOffset.MinValue
                    ? DateTimeOffset.UtcNow
                    : timeline.LastUpdatedTime;
                isLive = end <= start && maxSeek <= start;
            }

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props is not null)
                {
                    title = props.Title ?? "";
                    artist = props.Artist ?? "";

                    string key = title + "\u0000" + artist;
                    if (key != _artKey)
                    {
                        _artKey = key;
                        _cachedArt = null;
                        if (props.Thumbnail is not null)
                        {
                            using var stream = await props.Thumbnail.OpenReadAsync();
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream.AsStream();
                            bitmap.EndInit();
                            bitmap.Freeze();
                            _cachedArt = bitmap;
                            _cachedAccent = ExtractAccent(bitmap);
                        }
                        else
                        {
                            _cachedAccent = FallbackAccent;
                        }
                    }
                    accent = _cachedAccent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Media art/metadata failed: {ex.Message}");
                accent = _cachedAccent;
            }
        }

        if (!_disposed && version == Volatile.Read(ref _version))
        {
            StateChanged?.Invoke(this, new MediaSnapshot(
                title, artist, _cachedArt, accent, isPlaying, active, isLive, canSeek,
                canPlay, canPause, canNext, canPrevious,
                position, start, end, minSeek, maxSeek, rate, lastUpdated));
        }
    }

    public async Task PlayPauseAsync()
    {
        var session = _session;
        if (session is null) return;

        bool ok = await session.TryTogglePlayPauseAsync();
        Debug.WriteLine(ok ? "Play/pause accepted" : "Play/pause refused by app");
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null || session.GetPlaybackInfo()?.Controls.IsPreviousEnabled == false) return;
        bool ok = await session.TrySkipPreviousAsync();
        Debug.WriteLine(ok ? "Previous accepted" : "Previous refused by app");
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null || session.GetPlaybackInfo()?.Controls.IsNextEnabled == false) return;
        bool ok = await session.TrySkipNextAsync();
        Debug.WriteLine(ok ? "Next accepted" : "Next refused by app");
    }

    public async Task SeekAsync(TimeSpan absolutePosition)
    {
        var session = _session;
        if (session is null || session.GetPlaybackInfo()?.Controls.IsPlaybackPositionEnabled == false) return;

        var timeline = session.GetTimelineProperties();
        if (timeline is not null)
        {
            var low = timeline.MinSeekTime > TimeSpan.Zero ? timeline.MinSeekTime : timeline.StartTime;
            var high = timeline.MaxSeekTime > low ? timeline.MaxSeekTime : timeline.EndTime;
            if (high > low) absolutePosition = TimeSpan.FromTicks(Math.Clamp(absolutePosition.Ticks, low.Ticks, high.Ticks));
        }

        bool ok = await session.TryChangePlaybackPositionAsync(absolutePosition.Ticks);
        Debug.WriteLine(ok ? "Seek accepted" : "Seek refused by app");
        _ = PublishAsync();
    }

    private static Color ExtractAccent(BitmapSource source)
    {
        if (source.PixelWidth == 0 || source.PixelHeight == 0) return FallbackAccent;

        var scale = Math.Min(64.0 / source.PixelWidth, 64.0 / source.PixelHeight);
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var buckets = new Dictionary<int, int>();
        int step = Math.Max(1, width * height / 1600);
        for (int i = 0; i < width * height; i += step)
        {
            int o = i * 4;
            byte b = pixels[o], g = pixels[o + 1], r = pixels[o + 2];
            if (Math.Max(r, Math.Max(g, b)) < 45 || Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 25) continue;
            int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
            buckets[key] = buckets.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        if (buckets.Count == 0) return FallbackAccent;
        int best = 0;
        foreach (var pair in buckets) if (pair.Value > buckets[best]) best = pair.Key;
        return Color.FromArgb(255,
            (byte)(((best >> 6) & 7) * 36 + 18),
            (byte)(((best >> 3) & 7) * 36 + 18),
            (byte)((best & 7) * 36 + 18));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Attach(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnManagerChanged;
            _manager.SessionsChanged -= OnManagerChanged;
        }
        foreach (var session in _subscribed)
            session.PlaybackInfoChanged -= OnAnySessionChanged;
        _subscribed.Clear();
    }
}
