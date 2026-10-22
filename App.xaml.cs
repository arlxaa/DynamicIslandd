using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Helpers;

namespace DynamicIsland;

/// <summary>
/// Entry point: enforces a single instance, picks the render mode and keeps the app alive on stray errors.
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one island at a time (e.g. "Start with Windows" plus a manual launch).
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\DynamicIsland.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        // Software rendering keeps the GPU driver's DLLs and a Direct3D device out of the process
        // (tens of MB of RAM). For a window this small the CPU cost is negligible. See IslandConfig.
        if (IslandConfig.UseSoftwareRendering)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A status widget should not take itself down because one system API hiccupped: log it and keep running.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
