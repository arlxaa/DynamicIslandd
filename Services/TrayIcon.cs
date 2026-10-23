using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// The island's icon in the notification area, next to the clock: click it to hide or show the island, right-click
/// it for a menu. It sends its messages to the island's own window (see <see cref="HandleMessage"/>), so it needs no
/// window of its own, and it puts itself back when Explorer restarts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = NativeMethods.WM_APP + 10;

    private readonly IntPtr _hwnd;
    private readonly int _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _toolTip = "Dynamic Island";
    private bool _added;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;

        // The app's own icon, at the size the notification area uses (16 px at 100 % scaling, 20 px at 125 %, ...).
        int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        if (Environment.ProcessPath is { } exe
            && NativeMethods.SHDefExtractIcon(exe, 0, 0, out IntPtr icon, IntPtr.Zero, (uint)size) == 0
            && icon != IntPtr.Zero)
        {
            _icon = icon;
            _ownsIcon = true;
        }
        else
        {
            _icon = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION); // shared system icon: never destroyed
        }

        _added = Send(NativeMethods.NIM_ADD);
    }

    /// <summary>Left click.</summary>
    public event Action? Click;

    /// <summary>Right click.</summary>
    public event Action? MenuRequested;

    public string ToolTip
    {
        set
        {
            _toolTip = value;
            if (_added)
                Send(NativeMethods.NIM_MODIFY);
        }
    }

    /// <summary>Call from the window procedure. Returns true for the icon's own messages.</summary>
    public bool HandleMessage(int msg, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch (unchecked((int)(long)lParam)) // the mouse message (the classic, version-0 icon protocol)
            {
                case NativeMethods.WM_LBUTTONUP:
                    Click?.Invoke();
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    MenuRequested?.Invoke();
                    break;
            }
            return true;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            _added = Send(NativeMethods.NIM_ADD); // Explorer restarted, and with it the notification area

        return false;
    }

    private bool Send(int command)
    {
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _toolTip,
            szInfo = "",
            szInfoTitle = "",
        };
        return NativeMethods.Shell_NotifyIcon(command, ref data);
    }

    public void Dispose()
    {
        if (_added)
            Send(NativeMethods.NIM_DELETE);
        _added = false;
        if (_ownsIcon)
            NativeMethods.DestroyIcon(_icon);
    }
}
