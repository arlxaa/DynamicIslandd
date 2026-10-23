using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Controls;
using DynamicIsland.Helpers;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The timer: a page to pick a duration and start it, a countdown in the pill (on its own, or next to the song
/// that is playing), and a "Timer done" alert with a sound. The counting itself is done by <see cref="CountdownTimer"/>.
/// </summary>
public partial class MainWindow
{
    private readonly CountdownTimer _timer = new();
    private readonly OneShotTimer _alarmTimer = new();   // ends the alarm sound after IslandConfig.TimerSoundDuration
    private ProgressRing[] _timerRings = Array.Empty<ProgressRing>();
    private Storyboard? _alertPulse;
    private int _timerMinutes = 5;                       // the duration picked on the timer page
    private int _wheelDelta;                             // mouse-wheel travel not yet turned into minutes

    private void InitializeTimer(SavedState saved)
    {
        _timerRings = new[] { CompactTimerRing, CompactMediaTimerRing, TimerRing };
        _timerMinutes = Math.Clamp(saved.LastTimerMinutes, 1, IslandConfig.TimerMaxMinutes);

        var chipStyle = (Style)FindResource("ChipButton");
        foreach (int minutes in IslandConfig.TimerPresets)
        {
            var chip = new Button { Style = chipStyle, Content = minutes.ToString(CultureInfo.CurrentCulture), Tag = minutes };
            chip.Click += OnTimerPresetClick;
            TimerPresetPanel.Children.Add(chip);
        }

        // A timer that was running (or paused) when the app last closed carries on where it was.
        if (saved.Timer is { } timer)
            _timer.Restore(timer);
        _timer.Tick += OnTimerTick;
        _timer.PhaseChanged += OnTimerPhaseChanged;

        UpdateTimerSetup();
        UpdateTimerControls();
    }

    private void DisposeTimer()
    {
        _timer.Tick -= OnTimerTick;
        _timer.PhaseChanged -= OnTimerPhaseChanged;
        _timer.Dispose();
        _alarmTimer.Dispose();
        AlarmSound.Stop();
    }

    // ─────────────── Picking a duration ───────────────

    private void OnTimerMinusClick(object sender, RoutedEventArgs e) => StepTimerMinutes(-1);

    private void OnTimerPlusClick(object sender, RoutedEventArgs e) => StepTimerMinutes(+1);

    private void OnTimerSetupWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        // One notch = one step. Precision touchpads send many small deltas, so add them up first.
        _wheelDelta += e.Delta;
        while (Math.Abs(_wheelDelta) >= Mouse.MouseWheelDeltaForOneLine)
        {
            int direction = Math.Sign(_wheelDelta);
            _wheelDelta -= direction * Mouse.MouseWheelDeltaForOneLine;
            StepTimerMinutes(direction);
        }
    }

    /// <summary>A minute at a time up to an hour, then five minutes at a time.</summary>
    private void StepTimerMinutes(int direction)
    {
        int m = _timerMinutes;
        int next = direction > 0
            ? (m < 60 ? m + 1 : (m / 5 + 1) * 5)
            : (m <= 60 ? m - 1 : (m - 1) / 5 * 5);
        _timerMinutes = Math.Clamp(next, 1, IslandConfig.TimerMaxMinutes);
        UpdateTimerSetup();
    }

    private void OnTimerPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int minutes)
        {
            _timerMinutes = minutes;
            UpdateTimerSetup();
        }
    }

    private void UpdateTimerSetup()
    {
        TimerSetupText.Text = FormatTime(TimeSpan.FromMinutes(_timerMinutes));
        TimerMinusButton.IsEnabled = _timerMinutes > 1;
        TimerPlusButton.IsEnabled = _timerMinutes < IslandConfig.TimerMaxMinutes;

        // The chip that matches the picked duration lights up in the timer's orange.
        foreach (Button chip in TimerPresetPanel.Children)
        {
            bool picked = chip.Tag is int minutes && minutes == _timerMinutes;
            if (picked)
            {
                chip.Background = (Brush)FindResource("OrangeTintBrush");
                chip.Foreground = _orangeBrush;
            }
            else
            {
                chip.ClearValue(BackgroundProperty);
                chip.ClearValue(ForegroundProperty);
            }
        }
    }

    // ─────────────── Start, pause, cancel ───────────────

    private void OnTimerStartClick(object sender, RoutedEventArgs e) => _timer.Start(TimeSpan.FromMinutes(_timerMinutes));

    private void OnTimerPauseClick(object sender, RoutedEventArgs e)
    {
        if (_timer.Phase == TimerPhase.Running)
            _timer.Pause();
        else
            _timer.Resume();
    }

    private void OnTimerCancelClick(object sender, RoutedEventArgs e) => _timer.Stop();

    private void OnTimerRepeatClick(object sender, RoutedEventArgs e) => _timer.Start(_timer.Duration);

    private void OnTimerStopClick(object sender, RoutedEventArgs e) => _timer.Stop();

    // ─────────────── Countdown and alert ───────────────

    private void OnTimerTick() => UpdateTimerDisplay();

    private void OnTimerPhaseChanged()
    {
        if (_timer.Phase == TimerPhase.Ringing)
        {
            EndTyping(restoreFocus: true);
            TimerAlertDetail.Text = FormatDuration(_timer.Duration) + " timer";
            StartAlarm();
        }
        else
        {
            StopAlarm();
        }

        UpdateTimerControls();
        UpdateHiding(); // a ringing timer shows even over a fullscreen app, or when you hid the island
        UpdatePresentation();
        SaveState();
    }

    /// <summary>The countdown text and rings, everywhere they appear. Runs once a second while the timer runs.</summary>
    private void UpdateTimerDisplay()
    {
        TimeSpan left = _timer.Left;

        // Count the way a timer display does: a new 5-minute timer shows 5:00 for its first second, and 0:00 only when it rings.
        string text = FormatTime(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)));
        CompactTimerText.Text = text;
        CompactMediaTimerText.Text = text;
        TimerRunningText.Text = text;

        double progress = _timer.Duration > TimeSpan.Zero ? Math.Clamp(left / _timer.Duration, 0, 1) : 0;
        foreach (ProgressRing ring in _timerRings)
            ring.Progress = progress;
    }

    /// <summary>Everything that depends on whether the timer runs or is paused. Runs when that changes.</summary>
    private void UpdateTimerControls()
    {
        bool paused = _timer.Phase == TimerPhase.Paused;
        Brush digits = paused ? Brushes.White : _orangeBrush;
        CompactTimerText.Foreground = digits;
        CompactMediaTimerText.Foreground = digits;
        TimerRunningText.Foreground = digits;

        CompactTimerLabel.Text = paused ? "Paused" : "Timer";
        TimerRunningLabel.Text = paused
            ? "Paused"
            : "Ends at " + _timer.EndsAtUtc.ToLocalTime().ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

        // Pause (orange) turns into Resume (green), like the iOS Clock app.
        TimerPauseButton.Content = paused ? "Resume" : "Pause";
        TimerPauseButton.Background = (Brush)FindResource(paused ? "GreenTintBrush" : "OrangeTintBrush");
        TimerPauseButton.Foreground = paused ? _greenBrush : _orangeBrush;

        // Next to music, the countdown takes the equalizer's place in the pill (a minimal pill grows a little for it).
        CompactMediaTimer.Visibility = _timer.IsActive ? Visibility.Visible : Visibility.Collapsed;
        CompactEqualizer.Visibility = _timer.IsActive ? Visibility.Collapsed : Visibility.Visible;
        ApplyPillStyle();

        // A running timer tints its tab, so you can see it from the other pages.
        TimerTabIcon.Stroke = _timer.IsActive ? _orangeBrush : Brushes.White;

        UpdateTimerDisplay();
    }

    private void StartAlarm()
    {
        AlarmSound.Start(IslandConfig.TimerSound);
        _alertPulse ??= (Storyboard)FindResource("TimerAlertPulse");
        _alertPulse.Begin(this, isControllable: true);

        // After a while the sound and the pulse stop; the alert itself stays until you dismiss it.
        _alarmTimer.Schedule(IslandConfig.TimerSoundDuration, () =>
        {
            AlarmSound.Stop();
            _alertPulse.Stop(this);
        });
    }

    private void StopAlarm()
    {
        _alarmTimer.Cancel();
        AlarmSound.Stop();
        _alertPulse?.Stop(this);
    }

    /// <summary>"5 min", "1 h 30 min", "45 s".</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        var parts = new List<string>(3);
        if (duration.TotalHours >= 1)
            parts.Add((int)duration.TotalHours + " h");
        if (duration.Minutes > 0)
            parts.Add(duration.Minutes + " min");
        if (duration.Seconds > 0 || parts.Count == 0)
            parts.Add(duration.Seconds + " s");
        return string.Join(" ", parts);
    }
}
