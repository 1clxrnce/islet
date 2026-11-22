using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Color = System.Windows.Media.Color;

namespace DynamicIslandMusic;

public sealed record MediaSessionInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

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
    private const byte VkMediaNext = 0xB0;
    private const byte VkMediaPrevious = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyUp = 0x0002;

    private sealed record ArtEntry(ImageSource? Art, Color Accent);

    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private readonly Dictionary<string, ArtEntry> _artCache = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _lastPlaying;
    private string? _artKey;
    private ImageSource? _cachedArt;
    private Color _cachedAccent = FallbackAccent;
    private int _artLoading;
    private DateTimeOffset _artLastTry;
    private long _version;
    private int _publishing;
    private bool _disposed;

    public string? PreferredSessionId { get; set; }
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

    public void Refresh()
    {
        SelectSession();
        _ = PublishAsync();
    }

    // Cheap timeline resync for the timer; metadata and art stay cached.
    public void RefreshTimeline() => _ = PublishAsync();

    public IReadOnlyList<MediaSessionInfo> GetSessionInfos()
    {
        var list = new List<MediaSessionInfo>();
        if (_manager is null) return list;
        foreach (var session in _manager.GetSessions())
        {
            string id = session.SourceAppUserModelId ?? "";
            list.Add(new MediaSessionInfo(id, FriendlyName(id)));
        }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FriendlyName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Unknown app";
        int bang = id.IndexOf('!');
        string name = (bang >= 0 ? id[(bang + 1)..] : id).Replace('_', ' ').Trim();
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[^1].Length >= 10) name = string.Join(' ', parts[..^1]);
        return name.Length == 0 ? id : name;
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

    // Pinned source first, then the session actually playing, then the last one that played.
    private void SelectSession()
    {
        if (_manager is null) return;

        var sessions = _manager.GetSessions();
        var pinned = string.IsNullOrEmpty(PreferredSessionId)
            ? null
            : sessions.FirstOrDefault(s => s.SourceAppUserModelId == PreferredSessionId);
        var playing = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (playing is not null) _lastPlaying = playing;

        // GetSessions() can hand out a new wrapper for the same app, so match by id.
        var previous = _lastPlaying is null
            ? null
            : sessions.FirstOrDefault(s => s.SourceAppUserModelId == _lastPlaying.SourceAppUserModelId);

        var next = pinned
                   ?? playing
                   ?? previous
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

        // GetSessions() can return a fresh wrapper for the same app. That is not a new
        // track, so keep the cached art instead of reloading it on every manager event.
        bool sameApp = _session is not null && session is not null
                       && _session.SourceAppUserModelId == session.SourceAppUserModelId;

        _session = session;
        if (!sameApp)
        {
            _artKey = null;
            _cachedArt = null;
            _cachedAccent = FallbackAccent;
        }

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
        if (_disposed || Interlocked.Exchange(ref _publishing, 1) == 1) return;
        try { await PublishCoreAsync(); }
        finally { Volatile.Write(ref _publishing, 0); }
    }

    private async Task PublishCoreAsync()
    {
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
                    (title, artist) = CleanInfo(props.Title ?? "", props.Artist ?? "");
                    string key = title + "\u0000" + artist;
                    if (key != _artKey)
                    {
                        _artKey = key;
                        _artLastTry = DateTimeOffset.MinValue;
                        if (_artCache.TryGetValue(key, out var cached))
                        {
                            _cachedArt = cached.Art;
                            _cachedAccent = cached.Accent;
                        }
                        else
                        {
                            _cachedArt = null;
                            _cachedAccent = FallbackAccent;
                        }
                    }
                    // The thumbnail can arrive seconds after the metadata on browser sessions.
                    if (_cachedArt is null && DateTimeOffset.UtcNow - _artLastTry > TimeSpan.FromSeconds(1))
                        _ = LoadArtAsync(session, key);
                    accent = _cachedAccent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Media metadata failed: {ex.Message}");
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

    private async Task LoadArtAsync(GlobalSystemMediaTransportControlsSession session, string key)
    {
        if (_disposed || _artKey != key || Interlocked.Exchange(ref _artLoading, 1) == 1) return;
        _artLastTry = DateTimeOffset.UtcNow;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var thumbnail = props?.Thumbnail;
            if (thumbnail is not null)
            {
                using var stream = await thumbnail.OpenReadAsync();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream.AsStream();
                bitmap.EndInit();
                bitmap.Freeze();

                var accent = ExtractAccent(bitmap);
                if (_artCache.Count > 80) _artCache.Clear();
                _artCache[key] = new ArtEntry(bitmap, accent);
                if (!_disposed && _artKey == key)
                {
                    _cachedArt = bitmap;
                    _cachedAccent = accent;
                    _ = PublishAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Album art failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _artLoading, 0);
        }
    }

    private static (string Title, string Artist) CleanInfo(string title, string artist)
    {
        title = title.Trim();
        artist = Regex.Replace(artist.Trim(), @"\s+-\s+Topic\s*$", "", RegexOptions.IgnoreCase).Trim();

        if (artist.Length == 0)
        {
            int split = title.IndexOf(" - ", StringComparison.Ordinal);
            if (split > 0 && split < title.Length - 3)
            {
                artist = title[..split].Trim();
                title = title[(split + 3)..].Trim();
            }
        }
        return (title, artist);
    }

    // Each press resolves the session again: the previous one may have closed or changed.
    private GlobalSystemMediaTransportControlsSession? CurrentSession()
    {
        try { SelectSession(); }
        catch (Exception ex) { Debug.WriteLine($"Session lookup failed: {ex.Message}"); }
        return _session;
    }

    public async Task PlayPauseAsync()
    {
        var session = CurrentSession();
        if (session is null)
        {
            SendMediaKey(VkMediaPlayPause);
            return;
        }

        bool ok = false;
        try { ok = await session.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Play/pause failed: {ex.Message}"); }

        if (!ok) SendMediaKey(VkMediaPlayPause);
        RefreshTimeline();
    }

    public async Task PreviousAsync()
    {
        var session = CurrentSession();
        if (session is null)
        {
            SendMediaKey(VkMediaPrevious);
            return;
        }

        bool ok = false;
        try { ok = await session.TrySkipPreviousAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Previous failed: {ex.Message}"); }

        // Do not return early on a disabled flag: many apps report it but still accept keys.
        if (!ok) SendMediaKey(VkMediaPrevious);
        RefreshTimeline();
    }

    public async Task NextAsync()
    {
        var session = CurrentSession();
        if (session is null)
        {
            SendMediaKey(VkMediaNext);
            return;
        }

        bool ok = false;
        try { ok = await session.TrySkipNextAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Next failed: {ex.Message}"); }

        if (!ok) SendMediaKey(VkMediaNext);
        RefreshTimeline();
    }

    public async Task SeekAsync(TimeSpan absolutePosition)
    {
        SelectSession();
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

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
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
        int bestCount = -1;
        foreach (var pair in buckets) if (pair.Value > bestCount) { bestCount = pair.Value; best = pair.Key; }
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);
}
