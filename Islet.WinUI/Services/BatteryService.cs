using Windows.System.Power;

namespace Islet;

public enum BatteryAlert { PluggedIn, Unplugged, Low, Critical }

// Charger and battery level from Windows' power events (no polling). Laptops only; on a desktop HasBattery is false.
internal sealed class BatteryService : IDisposable
{
    private const int LowPercent = 20;
    private const int CriticalPercent = 10;

    private bool? _wasCharging;
    private int _lastWarned = 101;

    public BatteryService()
    {
        Read();
        _wasCharging = IsCharging;
        PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        PowerManager.BatteryStatusChanged += OnPowerChanged;
    }

    // Raised on the UI thread with the alert kind and the battery percentage.
    public event Action<BatteryAlert, int>? Alert;
    public event Action? Changed;

    public bool HasBattery { get; private set; }
    public bool IsCharging { get; private set; }
    public int Percent { get; private set; }

    // Windows raises these on a worker thread.
    private void OnPowerChanged(object? sender, object e) => Ui.Post(Check);

    private void Read()
    {
        HasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        IsCharging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        Percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
    }

    private void Check()
    {
        Read();
        Changed?.Invoke();
        if (!HasBattery) return;

        if (_wasCharging != IsCharging)
        {
            _wasCharging = IsCharging;
            Alert?.Invoke(IsCharging ? BatteryAlert.PluggedIn : BatteryAlert.Unplugged, Percent);
            if (IsCharging) _lastWarned = 101; // Warn again next time it runs low.
            return;
        }

        // Warn once when crossing 20%, once more at 10%; never while charging.
        if (IsCharging) return;
        if (Percent <= CriticalPercent && _lastWarned > CriticalPercent)
        {
            _lastWarned = Percent;
            Alert?.Invoke(BatteryAlert.Critical, Percent);
        }
        else if (Percent <= LowPercent && _lastWarned > LowPercent)
        {
            _lastWarned = Percent;
            Alert?.Invoke(BatteryAlert.Low, Percent);
        }
    }

    public void Dispose()
    {
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
    }
}
