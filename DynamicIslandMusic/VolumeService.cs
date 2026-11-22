using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIslandMusic;

// Wraps the default output device through Windows Core Audio. No packages needed.
internal sealed class VolumeService
{
    public double Volume { get; private set; } = 0.5;
    public bool IsMuted { get; private set; }

    private IAudioEndpointVolume? _endpoint;

    public void Attach()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (type is null) return;

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            // eConsole (0) is the device the taskbar volume slider controls.
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0) return;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out var endpoint) != 0) return;

            _endpoint = endpoint;
            Refresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio device unavailable: {ex.Message}");
        }
    }

    public void Refresh()
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
        }
    }

    public void SetVolume(double scalar)
    {
        Volume = Math.Clamp(scalar, 0, 1);
        if (_endpoint is not null)
        {
            int hr = _endpoint.SetMasterVolumeLevelScalar((float)Volume, IntPtr.Zero);
            if (hr != 0) Debug.WriteLine($"Setting volume failed: 0x{hr:X8}");
        }
        if (Volume > 0 && IsMuted) SetMute(false);
    }

    public void SetMute(bool mute)
    {
        IsMuted = mute;
        if (_endpoint is not null)
        {
            int hr = _endpoint.SetMute(mute, IntPtr.Zero);
            if (hr != 0) Debug.WriteLine($"Setting mute failed: 0x{hr:X8}");
        }
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // V-table order from mmdeviceapi.h: Activate, OpenPropertyStore, GetId, GetState.
    [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume endpoint);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out int count);
    [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(int channel, float level, IntPtr context);
    [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, IntPtr context);
    [PreserveSig] int GetChannelVolumeLevel(int channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
    [PreserveSig] int GetMute(out int mute);
    [PreserveSig] int GetVolumeStepInfo(out int step, out int count);
    [PreserveSig] int VolumeStepUp(IntPtr context);
    [PreserveSig] int VolumeStepDown(IntPtr context);
    [PreserveSig] int QueryHardwareSupport(out int mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
}
