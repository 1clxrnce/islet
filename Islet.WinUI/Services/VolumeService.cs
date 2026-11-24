using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Islet;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

// Master volume, per-app volume and output device switching through Windows Core Audio.
// Windows pushes volume and device changes to us; nothing here polls. Calls happen on the UI thread.
internal sealed class VolumeService : IDisposable
{
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly AudioVolumeCallback _volumeCallback;
    private readonly AudioDeviceCallback _deviceCallback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private IAudioSessionManager2? _sessions;
    private string? _deviceId;
    private bool _deviceWatchRegistered;

    // The playing app's audio sessions, found once and reused until something changes.
    private string? _appSessionsFor;
    private List<ISimpleAudioVolume> _appSessions = new();

    public VolumeService()
    {
        _volumeCallback = new AudioVolumeCallback(() => OnWindowsChanged(deviceChanged: false));
        _deviceCallback = new AudioDeviceCallback(() => OnWindowsChanged(deviceChanged: true));
    }

    // Raised on the UI thread when the volume, mute or default device changed.
    public event Action? Changed;

    public double Volume { get; private set; } = 0.5;
    public bool IsMuted { get; private set; }
    public string? DefaultDeviceId => _deviceId;

    public void Attach()
    {
        try
        {
            if (_enumerator is null)
            {
                var type = Type.GetTypeFromCLSID(AudioNative.EnumeratorClsid);
                if (type is null) return;
                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            }
            if (!_deviceWatchRegistered)
                _deviceWatchRegistered = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback) == 0;

            ReleaseEndpoint();
            if (_enumerator.GetDefaultAudioEndpoint(AudioNative.ERender, AudioNative.EConsole, out var device) != 0) return;
            if (device.GetId(out var id) == 0) _deviceId = id;

            Guid volumeIid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref volumeIid, AudioNative.ClsctxAll, IntPtr.Zero, out var endpoint) == 0)
            {
                _endpoint = (IAudioEndpointVolume)endpoint;
                _endpoint.RegisterControlChangeNotify(_volumeCallback);
            }

            Guid sessionsIid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref sessionsIid, AudioNative.ClsctxAll, IntPtr.Zero, out var sessions) == 0)
                _sessions = (IAudioSessionManager2)sessions;

            ReadLevels();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio device unavailable: {ex.Message}");
        }
    }

    private void ReleaseEndpoint()
    {
        if (_endpoint is not null)
        {
            try
            {
                _endpoint.UnregisterControlChangeNotify(_volumeCallback);
                Marshal.ReleaseComObject(_endpoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Releasing audio endpoint failed: {ex.Message}");
            }
        }
        _endpoint = null;
        _sessions = null;
        _deviceId = null;
        InvalidateAppSessions();
        _processNames.Clear();
    }

    // Re-reads the levels; Windows tells us when to (see AudioVolumeCallback).
    public void Refresh()
    {
        if (_endpoint is null) Attach();
        ReadLevels();
    }

    private void ReadLevels()
    {
        if (_endpoint is null) return;
        try
        {
            if (_endpoint.GetMasterVolumeLevelScalar(out float level) == 0) Volume = level;
            if (_endpoint.GetMute(out int mute) == 0) IsMuted = mute != 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading volume failed: {ex.Message}");
            _endpoint = null; // The device went away; re-attach on the next refresh.
        }
    }

    public void SetVolume(double scalar)
    {
        Volume = Math.Clamp(scalar, 0, 1);
        if (_endpoint is not null)
        {
            try
            {
                int hr = _endpoint.SetMasterVolumeLevelScalar((float)Volume, IntPtr.Zero);
                if (hr != 0) Debug.WriteLine($"Setting volume failed: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Setting volume failed: {ex.Message}");
            }
        }
        if (Volume > 0 && IsMuted) SetMute(false);
    }

    public void SetMute(bool mute)
    {
        IsMuted = mute;
        if (_endpoint is null) return;
        try
        {
            int hr = _endpoint.SetMute(mute, IntPtr.Zero);
            if (hr != 0) Debug.WriteLine($"Setting mute failed: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Setting mute failed: {ex.Message}");
        }
    }

    // Called from Windows' audio threads; hop to the UI thread before touching anything.
    private void OnWindowsChanged(bool deviceChanged)
    {
        Ui.Post(() =>
        {
            if (deviceChanged) Attach();
            else ReadLevels();
            Changed?.Invoke();
        });
    }

    #region Per-app volume

    // Forget the cached sessions: the app, the device or the app's audio streams changed.
    public void InvalidateAppSessions()
    {
        _appSessionsFor = null;
        _appSessions = new List<ISimpleAudioVolume>();
    }

    // Volume of the app's audio sessions (0..1), or null when it has none on this device.
    public double? GetAppVolume(string? appId)
    {
        foreach (var volume in AppSessions(appId))
        {
            try
            {
                if (volume.GetMasterVolume(out float level) == 0) return level;
            }
            catch (COMException)
            {
                InvalidateAppSessions(); // The session ended; find them again next time.
                return null;
            }
        }
        return null;
    }

    public void SetAppVolume(string? appId, double scalar)
    {
        float level = (float)Math.Clamp(scalar, 0, 1);
        // Browsers play through several processes; set every matching session.
        foreach (var volume in AppSessions(appId))
        {
            try
            {
                int hr = volume.SetMasterVolume(level, IntPtr.Zero);
                if (hr != 0) Debug.WriteLine($"Setting app volume failed: 0x{hr:X8}");
            }
            catch (COMException)
            {
                InvalidateAppSessions();
                return;
            }
        }
    }

    private List<ISimpleAudioVolume> AppSessions(string? appId)
    {
        if (appId != _appSessionsFor)
        {
            _appSessions = FindAppSessions(appId);
            _appSessionsFor = appId;
        }
        return _appSessions;
    }

    // shortcut: matches the media app id against audio-session process names, so Spotify, Chrome and
    // Edge work but apps whose id is a hash (Firefox) do not; upgrade by mapping ids to package names.
    private List<ISimpleAudioVolume> FindAppSessions(string? appId)
    {
        var found = new List<ISimpleAudioVolume>();
        if (_sessions is null || string.IsNullOrWhiteSpace(appId)) return found;

        try
        {
            if (_sessions.GetSessionEnumerator(out var sessions) != 0 || sessions.GetCount(out int count) != 0)
                return found;

            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0) continue;
                if (session is not IAudioSessionControl2 control || control.GetProcessId(out uint pid) != 0 || pid == 0)
                    continue;

                string name = ProcessName(pid);
                if (name.Length >= 3 && appId.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && session is ISimpleAudioVolume volume)
                {
                    found.Add(volume);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading audio sessions failed: {ex.Message}");
        }
        return found;
    }

    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            name = ""; // The process exited or is protected.
        }
        if (_processNames.Count > 200) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    #endregion

    #region Output devices

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var list = new List<AudioDevice>();
        if (_enumerator is null) return list;
        try
        {
            if (_enumerator.EnumAudioEndpoints(AudioNative.ERender, AudioNative.DeviceStateActive, out var devices) != 0
                || devices.GetCount(out int count) != 0)
                return list;

            for (int i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0 || device.GetId(out var id) != 0) continue;
                list.Add(new AudioDevice(id, FriendlyName(device) ?? "Audio device"));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Listing audio devices failed: {ex.Message}");
        }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(AudioNative.StgmRead, out var store) != 0) return null;
        var key = AudioNative.DeviceFriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.VarType == AudioNative.VtLpwstr ? Marshal.PtrToStringUni(value.Data) : null;
        }
        finally
        {
            AudioNative.PropVariantClear(ref value);
        }
    }

    public bool SetDefaultDevice(string id)
    {
        if (id == _deviceId) return true;
        try
        {
            var type = Type.GetTypeFromCLSID(AudioNative.PolicyConfigClsid);
            if (type is null || Activator.CreateInstance(type) is not IPolicyConfig policy) return false;

            int hr = policy.SetDefaultEndpoint(id, AudioNative.EConsole);
            if (hr == 0) hr = policy.SetDefaultEndpoint(id, AudioNative.EMultimedia);
            if (hr != 0)
            {
                Debug.WriteLine($"Switching output device failed: 0x{hr:X8}");
                return false;
            }
            Attach();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Switching output device failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    public void Dispose()
    {
        ReleaseEndpoint();
        if (_enumerator is not null && _deviceWatchRegistered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); }
            catch (Exception ex) { Debug.WriteLine($"Unregistering device watch failed: {ex.Message}"); }
            _deviceWatchRegistered = false;
        }
    }
}

// COM callback objects. Public so Windows can call into them; Windows holds references, so the
// service keeps them in fields for its whole lifetime.
public sealed class AudioVolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action _changed;
    internal AudioVolumeCallback(Action changed) => _changed = changed;

    public int OnNotify(IntPtr notifyData)
    {
        _changed();
        return 0;
    }
}

public sealed class AudioDeviceCallback : IMMNotificationClient
{
    private readonly Action _defaultChanged;
    internal AudioDeviceCallback(Action defaultChanged) => _defaultChanged = defaultChanged;

    public int OnDefaultDeviceChanged(int dataFlow, int role, string? defaultDeviceId)
    {
        if (dataFlow == AudioNative.ERender && role == AudioNative.EConsole) _defaultChanged();
        return 0;
    }

    public int OnDeviceStateChanged(string deviceId, int newState) => 0;
    public int OnDeviceAdded(string deviceId) => 0;
    public int OnDeviceRemoved(string deviceId) => 0;
    public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
}
