using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Follows the pointer while it is over the collapsed island. The island lets clicks through then
/// (WS_EX_TRANSPARENT), so it receives no mouse input of its own; a low-level mouse hook fills in.
/// <para>
/// The hook runs on its own thread, which does nothing else, so it can never slow down the system's mouse. And it
/// is only installed between <see cref="Start"/> and <see cref="Stop"/>, while the pointer is actually over the
/// island: the rest of the time it costs nothing. Its events are raised on the UI thread.
/// </para>
/// </summary>
internal sealed class PointerWatcher : IDisposable
{
    private const int StartMessage = NativeMethods.WM_APP + 1;
    private const int StopMessage = NativeMethods.WM_APP + 2;

    /// <summary>How far (in pixels) the pointer may drift and still count as resting.</summary>
    private const int RestSlop = 6;

    private sealed record Zone(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _hookProc; // kept in a field: Windows calls it, so the GC must not collect it
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _threadReady = new();
    private uint _threadId;
    private volatile Zone? _zone;  // written by the UI thread, read by the hook thread

    // Hook thread only:
    private IntPtr _hook;
    private bool _inside;
    private int _anchorX, _anchorY;

    public PointerWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Island pointer watcher" };
        _thread.Start();
        _threadReady.Wait();
    }

    /// <summary>The pointer left the watched area.</summary>
    public event Action? Left;

    /// <summary>A mouse button went down inside the area. The click itself goes to the window underneath.</summary>
    public event Action? Pressed;

    /// <summary>The pointer moved more than a few pixels inside the area, so it is not resting.</summary>
    public event Action? Moved;

    /// <summary>Starts watching an area of the screen (physical pixels) that the pointer is in right now.</summary>
    public void Start(int left, int top, int right, int bottom)
    {
        _zone = new Zone(left, top, right, bottom);
        NativeMethods.PostThreadMessage(_threadId, StartMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>The island changed shape while the pointer is on it.</summary>
    public void UpdateArea(int left, int top, int right, int bottom)
    {
        if (_zone is not null)
            _zone = new Zone(left, top, right, bottom);
    }

    public void Stop()
    {
        _zone = null;
        NativeMethods.PostThreadMessage(_threadId, StopMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates this thread's message queue
        _threadReady.Set();

        // The hook's callbacks are delivered while this loop waits in GetMessage.
        while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == StartMessage && _hook == IntPtr.Zero)
            {
                NativeMethods.GetCursorPos(out NativeMethods.POINT p);
                _inside = true;
                _anchorX = p.X;
                _anchorY = p.Y;
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, NativeMethods.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                    Log.Error($"Could not watch the pointer (SetWindowsHookEx error {Marshal.GetLastWin32Error()})");
            }
            else if (msg.message == StopMessage && _hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        // Kept tiny: Windows waits for this before the mouse moves on, for every app.
        if (code >= 0 && _zone is { } zone)
            Observe(zone, (int)wParam, Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Observe(Zone zone, int message, int x, int y)
    {
        if (!zone.Contains(x, y))
        {
            if (_inside)
            {
                _inside = false;
                Raise(Left);
            }
            return;
        }

        _inside = true;
        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_MBUTTONDOWN:
            case NativeMethods.WM_XBUTTONDOWN:
                Raise(Pressed);
                break;

            case NativeMethods.WM_MOUSEMOVE when Math.Abs(x - _anchorX) > RestSlop || Math.Abs(y - _anchorY) > RestSlop:
                _anchorX = x;
                _anchorY = y;
                Raise(Moved);
                break;
        }
    }

    private void Raise(Action? handler)
    {
        if (handler is not null)
            _dispatcher.BeginInvoke(DispatcherPriority.Input, handler);
    }

    public void Dispose()
    {
        _zone = null;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(1));
        _threadReady.Dispose();
    }
}
