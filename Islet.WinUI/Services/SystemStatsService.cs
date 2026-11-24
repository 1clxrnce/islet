using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Islet;

public sealed record StatsSample(double CpuPercent, double RamUsedGb, double RamTotalGb, double DownBytesPerSec, double UpBytesPerSec);

// CPU, RAM and network from built-in Windows APIs; nothing to install. Runs only between Start and Stop.
internal sealed class SystemStatsService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private long _idle, _kernel, _user;
    private long _received, _sent;
    private long _lastTicks;
    private bool _primed;
    private int _running;

    public SystemStatsService() => _timer = new System.Threading.Timer(_ => Sample());

    // Raised on a worker thread about once a second while running.
    public event Action<StatsSample>? Updated;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _primed = false;
        _timer.Change(0, 1000);
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private void Sample()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            GetSystemTimes(out long idle, out long kernel, out long user);
            var (received, sent) = NetworkTotals();

            if (!_primed)
            {
                // The first sample only sets the baseline: rates need two readings.
                (_idle, _kernel, _user, _received, _sent, _lastTicks) = (idle, kernel, user, received, sent, now);
                _primed = true;
                return;
            }

            double seconds = Math.Max(0.001, (now - _lastTicks) / (double)Stopwatch.Frequency);
            long busyTotal = (kernel - _kernel) + (user - _user); // Kernel time includes idle time.
            double cpu = busyTotal > 0 ? 100.0 * (1 - (idle - _idle) / (double)busyTotal) : 0;

            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            GlobalMemoryStatusEx(ref memory);
            const double gb = 1024.0 * 1024 * 1024;

            var sample = new StatsSample(
                Math.Clamp(cpu, 0, 100),
                (memory.TotalPhys - memory.AvailPhys) / gb,
                memory.TotalPhys / gb,
                Math.Max(0, received - _received) / seconds,
                Math.Max(0, sent - _sent) / seconds);

            (_idle, _kernel, _user, _received, _sent, _lastTicks) = (idle, kernel, user, received, sent, now);
            if (Volatile.Read(ref _running) == 1) Updated?.Invoke(sample);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading system stats failed: {ex.Message}");
        }
    }

    private static (long Received, long Sent) NetworkTotals()
    {
        long received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var stats = nic.GetIPStatistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }
        return (received, sent);
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
