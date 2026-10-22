using System.Windows.Threading;

namespace DynamicIsland.Helpers;

/// <summary>
/// A restartable "run this once after a delay", built on a single reusable <see cref="DispatcherTimer"/>.
/// Runs on the UI thread, and costs nothing while idle (a stopped DispatcherTimer holds no OS timer).
/// </summary>
internal sealed class OneShotTimer : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);
    private Action? _action;

    public OneShotTimer() => _timer.Tick += OnTick;

    public bool IsPending => _timer.IsEnabled;

    /// <summary>Starts (or restarts) the countdown. A previously scheduled action is replaced.</summary>
    public void Schedule(TimeSpan delay, Action action)
    {
        _timer.Stop();
        _action = action;
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _action = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Action? action = _action;
        _action = null;
        action?.Invoke();
    }

    public void Dispose()
    {
        Cancel();
        _timer.Tick -= OnTick;
    }
}
