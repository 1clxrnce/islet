using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Color = Windows.UI.Color;

namespace Islet;

public sealed record MediaSessionInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    byte[]? Art,       // Encoded image bytes; the UI decodes them at display size on its own thread.
    string? ArtHash,   // Changes only when the image does, so the UI decodes once per image.
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
    DateTimeOffset LastUpdated,
    string SourceAppId);

public sealed class MediaService : IDisposable
{
    public static readonly Color FallbackAccent = ColorHelper.FromArgb(255, 110, 110, 120);
    private const byte VkMediaNext = 0xB0;
    private const byte VkMediaPrevious = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyUp = 0x0002;

    private sealed record ArtEntry(byte[] Art, Color Accent, string Hash);

    private const int ArtCacheSize = 24;
    private static readonly TimeSpan StaleArtWindow = TimeSpan.FromSeconds(3); // How long a "same as last song" thumbnail is distrusted
    private static readonly TimeSpan LateArtWindow = TimeSpan.FromSeconds(6);  // How long to keep asking for a missing thumbnail
    private static readonly TimeSpan MetadataSafetyRefresh = TimeSpan.FromSeconds(5);

    private readonly object _sessionLock = new();
    private readonly object _artLock = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private readonly Dictionary<string, ArtEntry> _artCache = new();
    private readonly LinkedList<string> _artOrder = new(); // Most recently used first
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _lastPlaying;

    // Metadata is cached and re-read only when the app says it changed (plus a slow safety refresh).
    private string _title = "";
    private string _artist = "";
    private int _propsDirty = 1;
    private DateTimeOffset _propsReadAt = DateTimeOffset.MinValue;

    // Artwork belongs to one track; anything that arrives for another track is dropped.
    private string? _trackKey;
    private DateTimeOffset _trackChangedAt;
    private byte[]? _cachedArt;
    private Color _cachedAccent = FallbackAccent;
    private string? _artHash;          // Hash of the art on screen
    private string? _previousArtHash;  // Hash of the previous track's art, to spot stale thumbnails
    private int _artDirty = 1;
    private int _artLoading;

    private long _version;
    private int _publishing;
    private int _publishPending;
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
        Volatile.Write(ref _propsDirty, 1);
        SelectSession();
        _ = PublishAsync();
    }

    // Slow timer resync: re-reads the timeline (cheap); metadata only when stale.
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

    public static string FriendlyName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Unknown app";
        int bang = id.IndexOf('!');
        string name = (bang >= 0 ? id[(bang + 1)..] : id).Replace('_', ' ').Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[^1].Length >= 10) name = string.Join(' ', parts[..^1]);
        if (name.Length == 0) return id;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private void SubscribeAll()
    {
        if (_manager is null) return;

        lock (_sessionLock)
        {
            foreach (var old in _subscribed)
                old.PlaybackInfoChanged -= OnAnySessionChanged;
            _subscribed.Clear();

            foreach (var session in _manager.GetSessions())
            {
                session.PlaybackInfoChanged += OnAnySessionChanged;
                _subscribed.Add(session);
            }
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
    // Called from WinRT worker threads and the UI thread, so it runs under a lock.
    private void SelectSession()
    {
        if (_manager is null || _disposed) return;
        lock (_sessionLock) SelectSessionCore(_manager);
    }

    private void SelectSessionCore(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
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
                   ?? manager.GetCurrentSession();
        Attach(next);
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }

        bool sameApp = _session is not null && session is not null
                       && _session.SourceAppUserModelId == session.SourceAppUserModelId;
        _session = session;
        // A different app means different metadata; a fresh wrapper for the same app does not.
        if (!sameApp) Volatile.Write(ref _propsDirty, 1);

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnPropertiesChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnSessionChanged;
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _ = PublishAsync();

    // Title, artist or thumbnail changed. Browsers often send the thumbnail in a later event.
    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        Volatile.Write(ref _propsDirty, 1);
        Volatile.Write(ref _artDirty, 1);
        _ = PublishAsync();
    }

    // One publish runs at a time. A request that arrives mid-publish is not dropped:
    // it marks the state dirty and the running publish goes round once more.
    private async Task PublishAsync()
    {
        if (_disposed) return;
        Volatile.Write(ref _publishPending, 1);
        if (Interlocked.Exchange(ref _publishing, 1) == 1) return;
        try
        {
            while (!_disposed && Interlocked.Exchange(ref _publishPending, 0) == 1)
            {
                try { await PublishCoreAsync(); }
                catch (Exception ex) { Debug.WriteLine($"Publishing media state failed: {ex.Message}"); }
            }
        }
        finally
        {
            Volatile.Write(ref _publishing, 0);
        }
    }

    private async Task PublishCoreAsync()
    {
        long version = Interlocked.Increment(ref _version);

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
        string appId = session?.SourceAppUserModelId ?? "";
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

            if (Volatile.Read(ref _propsDirty) == 1 || DateTimeOffset.UtcNow - _propsReadAt > MetadataSafetyRefresh)
                await ReadPropertiesAsync(session, appId);
            if (Volatile.Read(ref _artDirty) == 1) _ = LoadArtAsync(session, _trackKey);
        }
        else
        {
            _title = "";
            _artist = "";
            OnTrackKey(null);
        }

        if (!_disposed && version == Volatile.Read(ref _version))
        {
            StateChanged?.Invoke(this, new MediaSnapshot(
                _title, _artist, _cachedArt, _artHash, _cachedAccent, isPlaying, active, isLive, canSeek,
                canPlay, canPause, canNext, canPrevious,
                position, start, end, minSeek, maxSeek, rate, lastUpdated, appId));
        }
    }

    private async Task ReadPropertiesAsync(GlobalSystemMediaTransportControlsSession session, string appId)
    {
        Volatile.Write(ref _propsDirty, 0);
        _propsReadAt = DateTimeOffset.UtcNow;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;
            (_title, _artist) = CleanInfo(props.Title ?? "", props.Artist ?? "");
            OnTrackKey(TrackKey(appId, _title, _artist));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media metadata failed: {ex.Message}");
        }
    }

    private static string TrackKey(string appId, string title, string artist) => appId + "\u0000" + title + "\u0000" + artist;

    // A new track: show cached art at once if we have trusted art for it, else a placeholder,
    // and always fetch the thumbnail again to confirm it.
    private void OnTrackKey(string? key)
    {
        if (key == _trackKey) return;
        _trackKey = key;
        _trackChangedAt = DateTimeOffset.UtcNow;
        _previousArtHash = _artHash;

        ArtEntry? cached = null;
        if (key is not null)
            lock (_artLock) _artCache.TryGetValue(key, out cached);

        _cachedArt = cached?.Art;
        _cachedAccent = cached?.Accent ?? FallbackAccent;
        _artHash = cached?.Hash;
        Volatile.Write(ref _artDirty, key is null ? 0 : 1);
    }

    private async Task LoadArtAsync(GlobalSystemMediaTransportControlsSession session, string? key)
    {
        if (_disposed || key is null || key != _trackKey || Interlocked.Exchange(ref _artLoading, 1) == 1) return;
        Volatile.Write(ref _artDirty, 0);
        bool retry = false;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            // The app may already be on the next track; this thumbnail would belong to it, not to key.
            var (title, artist) = CleanInfo(props.Title ?? "", props.Artist ?? "");
            if (TrackKey(session.SourceAppUserModelId ?? "", title, artist) != key) return;

            var thumbnail = props.Thumbnail;
            if (thumbnail is null)
            {
                retry = DateTimeOffset.UtcNow - _trackChangedAt < LateArtWindow; // Browsers publish art late.
                return;
            }

            byte[] bytes;
            using (var ras = await thumbnail.OpenReadAsync())
            using (var stream = ras.AsStream())
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }
            if (bytes.Length == 0 || key != _trackKey) return;

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (hash == _artHash) return; // Already showing exactly this image.

            // Same image as the previous song, right after a track change: most likely the app has
            // not updated its thumbnail yet. Wait; accept it later (songs on one album share art).
            if (hash == _previousArtHash && DateTimeOffset.UtcNow - _trackChangedAt < StaleArtWindow)
            {
                retry = true;
                return;
            }

            var accent = await ExtractAccentAsync(bytes);

            if (_disposed || key != _trackKey) return;
            Remember(key, new ArtEntry(bytes, accent, hash));
            _cachedArt = bytes;
            _cachedAccent = accent;
            _artHash = hash;
            _ = PublishAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Album art failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _artLoading, 0);
            if (retry && !_disposed) _ = RetryArtAsync(key);
        }
    }

    private async Task RetryArtAsync(string key)
    {
        await Task.Delay(700);
        if (_disposed || key != _trackKey) return;
        Volatile.Write(ref _artDirty, 1);
        _ = PublishAsync();
    }

    // Small least-recently-used cache, so replaying a song shows its art instantly.
    private void Remember(string key, ArtEntry entry)
    {
        lock (_artLock)
        {
            if (_artCache.ContainsKey(key)) _artOrder.Remove(key);
            _artCache[key] = entry;
            _artOrder.AddFirst(key);
            while (_artOrder.Count > ArtCacheSize)
            {
                _artCache.Remove(_artOrder.Last!.Value);
                _artOrder.RemoveLast();
            }
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
        var session = CurrentSession();
        if (session is null || session.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled == false) return;

        var timeline = session.GetTimelineProperties();
        if (timeline is not null)
        {
            var low = timeline.MinSeekTime > TimeSpan.Zero ? timeline.MinSeekTime : timeline.StartTime;
            var high = timeline.MaxSeekTime > low ? timeline.MaxSeekTime : timeline.EndTime;
            if (high > low) absolutePosition = TimeSpan.FromTicks(Math.Clamp(absolutePosition.Ticks, low.Ticks, high.Ticks));
        }

        try
        {
            bool ok = await session.TryChangePlaybackPositionAsync(absolutePosition.Ticks);
            if (!ok) Debug.WriteLine("Seek refused by app");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Seek failed: {ex.Message}");
        }
        _ = PublishAsync();
    }

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
    }

    // Decodes a 64 px copy off the UI thread (WIC via Windows.Graphics.Imaging) and picks the dominant vivid color.
    private static async Task<Color> ExtractAccentAsync(byte[] bytes)
    {
        byte[] pixels;
        int width, height;
        try
        {
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return FallbackAccent;

            double scale = Math.Min(1, Math.Min(64.0 / decoder.PixelWidth, 64.0 / decoder.PixelHeight));
            width = Math.Max(1, (int)(decoder.PixelWidth * scale));
            height = Math.Max(1, (int)(decoder.PixelHeight * scale));
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Linear },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            pixels = data.DetachPixelData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading album art colors failed: {ex.Message}");
            return FallbackAccent;
        }

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
        return ColorHelper.FromArgb(255,
            (byte)(((best >> 6) & 7) * 36 + 18),
            (byte)(((best >> 3) & 7) * 36 + 18),
            (byte)((best & 7) * 36 + 18));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnManagerChanged;
            _manager.SessionsChanged -= OnManagerChanged;
        }
        lock (_sessionLock)
        {
            Attach(null);
            foreach (var session in _subscribed)
                session.PlaybackInfoChanged -= OnAnySessionChanged;
            _subscribed.Clear();
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);
}
