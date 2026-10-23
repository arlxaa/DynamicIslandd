using System.Windows;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Getting to the island when it is out of sight, without hunting for the exe: hiding and showing it (from its own
/// menu or the notification-area icon), starting it again (which brings a running island back into view), and
/// "Start with Windows" (which also adds a Start menu entry).
/// </summary>
public partial class MainWindow
{
    private TrayIcon? _tray;
    private bool _hiddenByUser;              // "Hide island", from the island's menu or the tray icon
    private bool _startMenuShortcutCreated;  // made once; if you delete it, it stays deleted

    /// <summary>Needs the window handle, so it runs from OnSourceInitialized.</summary>
    private void InitializeTray()
    {
        HideHintText.Text = IslandConfig.ShowTrayIcon ? "Show it from the tray icon" : "Start it again to show it";
        if (!IslandConfig.ShowTrayIcon)
            return;
        _tray = new TrayIcon(_hwnd);
        _tray.Click += OnTrayClick;
        _tray.MenuRequested += ShowTrayMenu;
        UpdateTrayToolTip();
    }

    private void DisposeTray()
    {
        if (_tray is null)
            return;
        _tray.Click -= OnTrayClick;
        _tray.MenuRequested -= ShowTrayMenu;
        _tray.Dispose();
    }

    /// <summary>Hides the island (it keeps running, and a finished timer still shows) or brings it back.</summary>
    private void SetHiddenByUser(bool hidden)
    {
        if (_hiddenByUser == hidden)
            return;
        _hiddenByUser = hidden;
        UpdateTrayToolTip();
        UpdateHiding();
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is not null)
            _tray.ToolTip = _hiddenByUser ? "Dynamic Island (hidden): click to show" : "Dynamic Island: click to hide";
    }

    private void OnTrayClick() => SetHiddenByUser(!_hiddenByUser);

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        SetHiddenByUser(true);
    }

    /// <summary>
    /// Dynamic Island was started again while it runs (from the Start menu, say): show it, and open it for a moment
    /// so you can see where it is.
    /// </summary>
    public void ShowOnRequest()
    {
        SetHiddenByUser(false);
        if (_hidden || _expanded)
            return; // a fullscreen app is in front, or it is already open
        SetExpanded(true);
        _hoverTimer.Schedule(TimeSpan.FromSeconds(2.5), () =>
        {
            if (!HitZone.IsMouseOver && !_typing)
                SetExpanded(false);
        });
    }

    /// <summary>The tray icon's menu: a standard Windows menu, like every other notification-area icon has.</summary>
    private void ShowTrayMenu()
    {
        const int ShowHideItem = 1, StartupItem = 2, QuitItem = 3;

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, ShowHideItem, _hiddenByUser ? "Show Dynamic Island" : "Hide Dynamic Island");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | (StartupManager.IsEnabled ? NativeMethods.MF_CHECKED : 0), StartupItem, "Start with Windows");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, QuitItem, "Quit Dynamic Island");

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            IntPtr previousForeground = NativeMethods.GetForegroundWindow();

            // A popup menu only closes properly on an outside click when its owner is in the foreground (KB135788).
            NativeMethods.SetForegroundWindow(_hwnd);
            int command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case ShowHideItem:
                    SetHiddenByUser(!_hiddenByUser);
                    break;
                case StartupItem:
                    SetStartWithWindows(!StartupManager.IsEnabled);
                    break;
                case QuitItem:
                    Close();
                    return;
            }

            // Give the keyboard back to where it was (unless you already clicked somewhere else).
            if (NativeMethods.GetForegroundWindow() == _hwnd && previousForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(previousForeground);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>"Start with Windows", from either menu. Turning it on also adds Dynamic Island to the Start menu.</summary>
    private void SetStartWithWindows(bool on)
    {
        StartupManager.IsEnabled = on;
        if (on && StartupManager.IsEnabled)
        {
            StartMenuShortcut.CreateOrUpdate();
            _startMenuShortcutCreated = true;
            SaveState();
        }
    }

    /// <summary>
    /// At startup: the copy that starts with Windows gets a Start menu entry (once), and an existing entry follows the
    /// exe if you moved it.
    /// </summary>
    private void EnsureStartMenuShortcut()
    {
        if (!StartupManager.IsEnabled)
            return;
        if (!_startMenuShortcutCreated || (StartMenuShortcut.Exists && !StartMenuShortcut.PointsHere))
        {
            StartMenuShortcut.CreateOrUpdate();
            _startMenuShortcutCreated = true;
            SaveState();
        }
    }
}
