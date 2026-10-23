using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Interop;

/// <summary>
/// The Win32 functions the island needs: window styles, focus, z-order, the pointer hook, the notification-area
/// icon and its menu, power notifications, fullscreen detection, the timer sound and working-set trimming.
/// </summary>
internal static class NativeMethods
{
    // ─────────────── Window styles & z-order ───────────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020; // with WS_EX_LAYERED: the mouse goes straight through to the window underneath
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

    public const int WM_NULL = 0x0000;
    public const int WM_QUIT = 0x0012;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_TIMECHANGE = 0x001E;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3;
    public const int SPI_SETWORKAREA = 0x002F;
    public const int SPI_GETWORKAREA = 0x0030;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ─────────────── Geometry ───────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

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

    // ─────────────── Pointer hook (see Services/PointerWatcher.cs) ───────────────

    public const int WH_MOUSE_LL = 14;

    /// <summary>lParam points to an MSLLHOOKSTRUCT, which starts with the pointer position (POINT).</summary>
    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookType, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hWnd, int filterMin, int filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, int filterMin, int filterMax, int remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ─────────────── Notification-area icon and its menu (see Services/TrayIcon.cs) ───────────────

    public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    public const int SM_CXSMICON = 49;
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    /// <summary>"TaskbarCreated" is broadcast when Explorer (re)starts; notification-area icons must then be added again.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    public static extern int RegisterWindowMessage(string name);

    /// <summary>Extracts an icon from a file at an exact size (low word of <paramref name="size"/>, in pixels).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")]
    public static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr largeIcon, IntPtr smallIcon, uint size);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    public const uint MF_STRING = 0x0000;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menu);

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
