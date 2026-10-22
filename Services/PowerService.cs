using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>Battery snapshot. <see cref="HasBattery"/> is false on desktop PCs.</summary>
public readonly record struct BatteryStatus(bool HasBattery, int Percent, bool IsCharging, bool IsPluggedIn, bool IsSaverOn);

/// <summary>
/// Battery level and charger state. Windows pushes changes to our window (WM_POWERBROADCAST), so nothing is
/// polled. The window forwards those messages to <see cref="Refresh"/>.
/// </summary>
public sealed class PowerService : IDisposable
{
    private static readonly Guid BatteryPercentageRemaining = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    private readonly List<IntPtr> _registrations = new();

    public BatteryStatus Current { get; private set; }

    /// <summary>Raised with (previous, current) whenever anything about the battery changes.</summary>
    public event Action<BatteryStatus, BatteryStatus>? Changed;

    /// <summary>Reads the initial state and asks Windows to notify <paramref name="hwnd"/> about every 1 % step and charger change.</summary>
    public void Attach(IntPtr hwnd)
    {
        Current = Query();
        Register(hwnd, BatteryPercentageRemaining);
        Register(hwnd, AcDcPowerSource);
    }

    private void Register(IntPtr hwnd, Guid setting)
    {
        IntPtr handle = NativeMethods.RegisterPowerSettingNotification(hwnd, ref setting, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (handle != IntPtr.Zero)
            _registrations.Add(handle);
    }

    /// <summary>Re-reads the battery state and raises <see cref="Changed"/> only if something differs.</summary>
    public void Refresh()
    {
        BatteryStatus now = Query();
        if (now == Current)
            return;
        BatteryStatus previous = Current;
        Current = now;
        Changed?.Invoke(previous, now);
    }

    private static BatteryStatus Query()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s))
            return default;

        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;
        return new BatteryStatus(
            HasBattery: hasBattery,
            Percent: hasBattery ? s.BatteryLifePercent : 0,
            IsCharging: hasBattery && (s.BatteryFlag & 8) != 0,
            IsPluggedIn: s.ACLineStatus == 1,
            IsSaverOn: (s.SystemStatusFlag & 1) != 0);
    }

    public void Dispose()
    {
        foreach (IntPtr handle in _registrations)
            NativeMethods.UnregisterPowerSettingNotification(handle);
        _registrations.Clear();
        Changed = null;
    }
}
