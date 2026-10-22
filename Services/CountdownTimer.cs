using System.Windows.Threading;

namespace DynamicIsland.Services;

public enum TimerPhase
{
    /// <summary>No timer.</summary>
    Off,
    Running,
    Paused,

    /// <summary>It reached zero and waits for you to stop or repeat it.</summary>
    Ringing,
}

/// <summary>
/// A countdown timer that measures wall-clock time (it remembers when it ends, in UTC), so it stays correct
/// through sleep, hibernation and app restarts. While it runs it wakes up once per displayed second, just after
/// each second boundary; when it is off, paused or ringing it costs nothing.
/// </summary>
public sealed class CountdownTimer : IDisposable
{
    private readonly DispatcherTimer _ticker = new(DispatcherPriority.Normal);
    private DateTime _endsAtUtc;     // while running: when it reaches zero
    private TimeSpan _pausedLeft;    // while paused: what was left

    public CountdownTimer() => _ticker.Tick += OnTick;

    public TimerPhase Phase { get; private set; }

    /// <summary>The length this timer was started with (for the progress ring, and for "Repeat").</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Running or paused: there is a countdown to show.</summary>
    public bool IsActive => Phase is TimerPhase.Running or TimerPhase.Paused;

    /// <summary>The exact time left. Zero unless the timer is running or paused.</summary>
    public TimeSpan Left => Phase switch
    {
        TimerPhase.Running => _endsAtUtc > DateTime.UtcNow ? _endsAtUtc - DateTime.UtcNow : TimeSpan.Zero,
        TimerPhase.Paused => _pausedLeft,
        _ => TimeSpan.Zero,
    };

    /// <summary>When a running timer goes off.</summary>
    public DateTime EndsAtUtc => _endsAtUtc;

    /// <summary>Raised once per displayed second while the timer runs.</summary>
    public event Action? Tick;

    /// <summary>Raised when the timer starts, pauses, resumes, stops or starts ringing.</summary>
    public event Action? PhaseChanged;

    /// <summary>Starts a new countdown (this also restarts a running or ringing timer).</summary>
    public void Start(TimeSpan duration)
    {
        Duration = duration;
        _endsAtUtc = DateTime.UtcNow + duration;
        SetPhase(TimerPhase.Running);
        ScheduleTick();
    }

    public void Pause()
    {
        if (Phase != TimerPhase.Running)
            return;
        _pausedLeft = Left;
        _ticker.Stop();
        SetPhase(TimerPhase.Paused);
    }

    public void Resume()
    {
        if (Phase != TimerPhase.Paused)
            return;
        _endsAtUtc = DateTime.UtcNow + _pausedLeft;
        SetPhase(TimerPhase.Running);
        ScheduleTick();
    }

    /// <summary>Cancels a running or paused timer, or silences a ringing one.</summary>
    public void Stop()
    {
        if (Phase == TimerPhase.Off)
            return;
        _ticker.Stop();
        SetPhase(TimerPhase.Off);
    }

    /// <summary>Re-checks the clock, e.g. after the PC wakes up: a timer that ran out in the meantime rings now.</summary>
    public void Refresh()
    {
        if (Phase == TimerPhase.Running)
            ScheduleTick();
    }

    /// <summary>What to save so the timer survives a restart of the app (null when there is nothing to keep).</summary>
    public SavedTimer? Save() => Phase switch
    {
        TimerPhase.Running => new SavedTimer((int)Duration.TotalSeconds, _endsAtUtc, null),
        TimerPhase.Paused => new SavedTimer((int)Duration.TotalSeconds, null, (int)Math.Ceiling(_pausedLeft.TotalSeconds)),
        _ => null,
    };

    /// <summary>Picks up a saved timer. One that ran out while the app was closed is dropped rather than ringing late.</summary>
    public void Restore(SavedTimer saved)
    {
        Duration = TimeSpan.FromSeconds(Math.Max(1, saved.DurationSeconds));
        if (saved.PausedLeftSeconds is int left and > 0)
        {
            _pausedLeft = TimeSpan.FromSeconds(Math.Min(left, Duration.TotalSeconds));
            SetPhase(TimerPhase.Paused);
        }
        else if (saved.EndsAtUtc is { } end && end.ToUniversalTime() > DateTime.UtcNow)
        {
            _endsAtUtc = end.ToUniversalTime();
            SetPhase(TimerPhase.Running);
            ScheduleTick();
        }
    }

    private void ScheduleTick()
    {
        _ticker.Stop();
        TimeSpan left = _endsAtUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            SetPhase(TimerPhase.Ringing);
            return;
        }

        // The display shows whole seconds, so wake up just after the next whole second of time left.
        double toNextSecond = left.TotalMilliseconds % 1000;
        if (toNextSecond < 1)
            toNextSecond = 1000;
        _ticker.Interval = TimeSpan.FromMilliseconds(toNextSecond + 15);
        _ticker.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        ScheduleTick();
        if (Phase == TimerPhase.Running)
            Tick?.Invoke();
    }

    private void SetPhase(TimerPhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke();
    }

    public void Dispose()
    {
        _ticker.Stop();
        _ticker.Tick -= OnTick;
    }
}
