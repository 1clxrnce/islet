using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace Islet;

// Null means the sensor could not be read (missing hardware, or it needs admin + PawnIO).
public sealed record TemperatureSample(float? Cpu, float? Gpu, float? Drive, float? Board, bool IsElevated);

// Temperatures from LibreHardwareMonitorLib (PawnIO-based versions) plus NVIDIA's own NVML for the GPU.
// Loaded lazily on a worker thread the first time the System page is shown; reads at most once a second.
internal sealed class TemperatureService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private Computer? _computer;
    private bool _opened;
    private bool _nvmlReady;
    private IntPtr _nvmlDevice;
    private int _running;

    public TemperatureService() => _timer = new System.Threading.Timer(_ => Sample());

    public static bool IsElevated { get; } = CheckElevated();

    // Raised on a worker thread while running.
    public event Action<TemperatureSample>? Updated;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _timer.Change(0, 2000);
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private void Sample()
    {
        // The timer can overlap a slow read; skip instead of queueing.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            EnsureOpen();
            float? cpu = null, gpu = NvidiaTemperature(), drive = null, board = null;

            if (_computer is not null)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();
                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            cpu ??= PreferredTemperature(hardware, "Package", "Tctl", "Tdie", "Core Average");
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            gpu ??= PreferredTemperature(hardware, "GPU Core", "Core");
                            break;
                        case HardwareType.Storage:
                            drive ??= PreferredTemperature(hardware);
                            break;
                        case HardwareType.Motherboard:
                            foreach (var sub in hardware.SubHardware)
                            {
                                sub.Update();
                                board ??= PreferredTemperature(sub, "System", "Motherboard");
                            }
                            break;
                    }
                }
            }

            if (Volatile.Read(ref _running) == 1)
                Updated?.Invoke(new TemperatureSample(cpu, gpu, drive, board, IsElevated));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading temperatures failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void EnsureOpen()
    {
        if (_opened) return;
        _opened = true;

        try
        {
            _nvmlReady = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out _nvmlDevice) == 0;
        }
        catch (Exception)
        {
            _nvmlReady = false; // No NVIDIA driver (DllNotFoundException) or no NVIDIA GPU.
        }

        try
        {
            // Without admin rights + PawnIO the CPU sensors simply stay empty; nothing is prompted.
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = !_nvmlReady,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true
            };
            computer.Open();
            _computer = computer;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Hardware monitor unavailable: {ex.Message}");
        }
    }

    // The first temperature sensor whose name contains one of the hints, else the hottest one.
    private static float? PreferredTemperature(IHardware hardware, params string[] nameHints)
    {
        float? hottest = null;
        foreach (var hint in nameHints)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value is > 0
                    && sensor.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return sensor.Value;
            }
        }
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value is > 0 && (hottest is null || sensor.Value > hottest))
                hottest = sensor.Value;
        }
        return hottest;
    }

    private float? NvidiaTemperature()
    {
        if (!_nvmlReady) return null;
        try
        {
            return nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint celsius) == 0 ? celsius : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        lock (_gate)
        {
            try { _computer?.Close(); }
            catch (Exception ex) { Debug.WriteLine($"Closing hardware monitor failed: {ex.Message}"); }
            _computer = null;
            if (_nvmlReady)
            {
                try { nvmlShutdown(); } catch (Exception) { }
                _nvmlReady = false;
            }
        }
    }

    // NVML ships with the NVIDIA driver (System32\nvml.dll) and needs no admin rights.
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);
}
