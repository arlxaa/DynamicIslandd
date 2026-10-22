using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Interop;

/// <summary>
/// The Win32 functions the island needs: window styles, focus, z-order, power notifications, fullscreen
/// detection, the timer sound and working-set trimming.
/// </summary>
internal static class NativeMethods
{
    // ─────────────── Window styles & z-order ───────────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // not in Alt+Tab, not in the taskbar, visible on every virtual desktop
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;  // clicking the island never steals focus from your app

    // GetWindowLong/SetWindowLong (not the ...Ptr variants) are fine for GWL_EXSTYLE on both x86 and x64.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    // ─────────────── Window messages ───────────────

    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_TIMECHANGE = 0x001E;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int MA_NOACTIVATE = 3;
    public const int SPI_SETWORKAREA = 0x002F;
    public const int SPI_GETWORKAREA = 0x0030;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    // ─────────────── Geometry ───────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(int action, int param, out RECT result, int winIni);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Gives a window keyboard focus. Windows only allows this to the app that received the last input, which is
    /// the island right after you click it (used to let you type a to-do, then to hand focus back).
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    private static readonly StringBuilder ClassNameBuffer = new(64); // reused: no allocation per check

    /// <summary>
    /// True when the foreground window covers the whole primary monitor (games, fullscreen video, F11 browsers),
    /// but not for the desktop, the taskbar/Start menu, or ordinary maximized windows.
    /// </summary>
    public static bool IsFullscreenAppInForeground(IntPtr ownWindow)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow || fg == GetShellWindow() || IsZoomed(fg))
            return false;

        ClassNameBuffer.Clear();
        GetClassName(fg, ClassNameBuffer, ClassNameBuffer.Capacity);
        switch (ClassNameBuffer.ToString())
        {
            case "Progman":                      // desktop
            case "WorkerW":                      // desktop (wallpaper host)
            case "Shell_TrayWnd":                // taskbar
            case "Shell_SecondaryTrayWnd":       // taskbar on other monitors
            case "Windows.UI.Core.CoreWindow":   // Start, Search, notification center
                return false;
        }

        if (!GetWindowRect(fg, out RECT r))
            return false;

        IntPtr monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONULL);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || (info.dwFlags & MONITORINFOF_PRIMARY) == 0)
            return false; // the island only lives on the primary monitor

        return r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
            && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
    }

    // ─────────────── Sound ───────────────

    public const uint SND_ASYNC = 0x0001;         // return at once and play in the background
    public const uint SND_NODEFAULT = 0x0002;     // stay silent (no default beep) if the sound can't be found
    public const uint SND_LOOP = 0x0008;          // repeat until stopped (needs SND_ASYNC)
    public const uint SND_ALIAS = 0x00010000;     // a system sound name, such as "SystemExclamation"
    public const uint SND_FILENAME = 0x00020000;  // a path to a .wav file

    /// <summary>Plays a .wav file or a system sound. Passing null stops whatever this app is playing.</summary>
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    // ─────────────── Power ───────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = on battery, 1 = plugged in, 255 = unknown
        public byte BatteryFlag;         // bit 8 = charging, 128 = no battery, 255 = unknown
        public byte BatteryLifePercent;  // 0-100, 255 = unknown
        public byte SystemStatusFlag;    // 1 = battery saver on
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // ─────────────── Memory ───────────────

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>With (-1, -1) Windows removes as many pages as possible from the working set.</summary>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimumSize, nint maximumSize);
}
