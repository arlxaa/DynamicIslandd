using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The island: a borderless, transparent, always-on-top window whose only visible part is a black shape that
/// morphs between three states.
/// <list type="bullet">
/// <item><b>Dot</b>: idle, when nothing is playing and there is nothing to announce.</item>
/// <item><b>Compact</b>: a pill with one live activity (now playing, a volume change, or the charger being plugged in).</item>
/// <item><b>Expanded</b>: on hover or click, a card with clock, date, battery, weather and full media controls.</item>
/// </list>
/// Everything is event-driven. The only always-on timer fires once per minute for the clock.
/// </summary>
public partial class MainWindow : Window
{
    private enum Hud { None, Volume, Charging }

    // ─────────────── Animation curves (frozen: shared by every animation at no cost) ───────────────

    private static readonly IEasingFunction SpringCurve = Frozen(new SpringEase { DampingRatio = 0.72, Stiffness = 8.5 });
    private static readonly IEasingFunction SettleCurve = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction FadeCurve = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan ShrinkToDotDuration = TimeSpan.FromMilliseconds(380);

    // ─────────────── Services (each one releases its OS hooks in Dispose) ───────────────

    private readonly MediaService _media;
    private readonly AudioVolumeService _volume;
    private readonly PowerService _power = new();
    private readonly WeatherService? _weather;

    // ─────────────── Timers (DispatcherTimers tick on the UI thread and cost nothing while stopped) ───────────────

    private readonly DispatcherTimer _minuteTimer = new(DispatcherPriority.Normal);           // clock + weather schedule
    private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Normal)          // only while the player card is visible and playing
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private readonly DispatcherTimer _fullscreenTimer = new(DispatcherPriority.Background)    // only if HideWhenFullscreen
    {
        Interval = IslandConfig.FullscreenCheckInterval,
    };
    private readonly OneShotTimer _hoverTimer = new();   // hover intent before expanding / grace period before collapsing
    private readonly OneShotTimer _hudTimer = new();     // how long a volume or charging HUD stays up
    private readonly OneShotTimer _pausedTimer = new();  // paused media keeps the pill for a few seconds
    private readonly OneShotTimer _trimTimer = new();    // gives unused memory back once things have settled

    // ─────────────── What the island shows (inputs of ChooseView) ───────────────

    private bool _expanded;              // the user is hovering over or has clicked the island
    private Hud _hud = Hud.None;         // a transient announcement is up
    private bool _showMediaPill;         // music is playing (or was paused only moments ago)
    private bool _hiddenForFullscreen;   // a fullscreen app is in front: stay out of the way
    private FrameworkElement? _activeView; // null = dot
    private readonly FrameworkElement[] _views;

    // ─────────────── Misc ───────────────

    private readonly SolidColorBrush _accentBrush = new(Colors.White);
    private Color _accentTarget = Colors.White;
    private readonly RectangleGeometry _stageClip = new();
    private readonly Brush _greenBrush, _redBrush, _yellowBrush;
    private Storyboard? _equalizer;
    private bool _equalizerRunning;
    private int _minutesSinceWeather;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        _views = new FrameworkElement[] { CompactMediaView, VolumeHudView, ChargingHudView, ExpandedMediaView, ExpandedIdleView };
        _greenBrush = (Brush)FindResource("GreenBrush");
        _redBrush = (Brush)FindResource("RedBrush");
        _yellowBrush = (Brush)FindResource("YellowBrush");

        // One animatable brush, tinted with the album art's colour, shared by the equalizer and the progress bar.
        // It is assigned as a local value on purpose: WPF may freeze brushes that are shared through resource
        // dictionaries and styles, and a frozen brush can't be animated.
        foreach (var bar in new[] { EqBar1, EqBar2, EqBar3, EqBar4, EqBar5, EqBar6, EqBar7, EqBar8 })
            bar.Fill = _accentBrush;
        ProgressFill.Background = _accentBrush;

        Stage.Clip = _stageClip;
        Stage.SizeChanged += OnStageSizeChanged;
        if (IslandConfig.HoverPadding <= 0)
            HitZone.Background = Brushes.Transparent; // only the visible island catches the mouse

        _media = new MediaService(Dispatcher, IslandConfig.MediaSource);
        _media.Changed += OnMediaChanged;
        _volume = new AudioVolumeService(Dispatcher);
        _volume.VolumeChanged += OnVolumeChanged;
        _power.Changed += OnBatteryChanged;
        if (!string.IsNullOrWhiteSpace(IslandConfig.WeatherCity))
            _weather = new WeatherService(IslandConfig.WeatherCity.Trim(), IslandConfig.UseFahrenheit);

        _minuteTimer.Tick += OnMinuteTick;
        _progressTimer.Tick += OnProgressTick;
        _fullscreenTimer.Tick += OnFullscreenTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ═══════════════════════════════ Lifecycle ═══════════════════════════════

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not in Alt+Tab or the taskbar, and shown on every virtual desktop.
        // No-activate: clicking the island never steals keyboard focus from the app you are working in.
        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        _power.Attach(_hwnd); // battery notifications arrive as window messages
        PositionAtTopCenter();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _equalizer = (Storyboard)FindResource("EqualizerStoryboard");

        UpdateClock();
        ScheduleNextMinuteTick();
        _minuteTimer.Start();
        ApplyBattery(_power.Current);
        if (IslandConfig.HideWhenFullscreen)
            _fullscreenTimer.Start();

        _volume.Start();
        if (_weather is not null)
            _ = RefreshWeatherAsync();
        if (IslandConfig.TrimMemoryWhenIdle)
            _trimTimer.Schedule(TimeSpan.FromSeconds(10), TrimMemory);

        await _media.StartAsync(); // if music is already playing, OnMediaChanged morphs us into the pill
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Stop every timer and detach every OS hook, so nothing keeps running or holds references after exit.
        _minuteTimer.Stop();
        _minuteTimer.Tick -= OnMinuteTick;
        _progressTimer.Stop();
        _progressTimer.Tick -= OnProgressTick;
        _fullscreenTimer.Stop();
        _fullscreenTimer.Tick -= OnFullscreenTick;
        _hoverTimer.Dispose();
        _hudTimer.Dispose();
        _pausedTimer.Dispose();
        _trimTimer.Dispose();
        if (_equalizerRunning)
            _equalizer?.Stop(this);

        _media.Changed -= OnMediaChanged;
        _media.Dispose();
        _volume.VolumeChanged -= OnVolumeChanged;
        _volume.Dispose();
        _power.Changed -= OnBatteryChanged;
        _power.Dispose();
        _weather?.Dispose();

        Stage.SizeChanged -= OnStageSizeChanged;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                // Belt and braces on top of WS_EX_NOACTIVATE: a click never activates the island.
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_POWERBROADCAST:
                _power.Refresh(); // raises OnBatteryChanged only if something actually changed
                if ((long)wParam == NativeMethods.PBT_APMRESUMEAUTOMATIC)
                    Dispatcher.InvokeAsync(OnResumedFromSleep, DispatcherPriority.Background);
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
                Dispatcher.InvokeAsync(PositionAtTopCenter, DispatcherPriority.Background);
                break;

            case NativeMethods.WM_TIMECHANGE:
                TimeZoneInfo.ClearCachedData(); // the clock or the time zone was changed
                Dispatcher.InvokeAsync(OnResumedFromSleep, DispatcherPriority.Background);
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                if ((long)wParam == NativeMethods.SPI_SETWORKAREA)
                {
                    Dispatcher.InvokeAsync(PositionAtTopCenter, DispatcherPriority.Background); // taskbar moved or resized
                }
                else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "intl")
                {
                    CultureInfo.CurrentCulture.ClearCachedData(); // 12/24-hour or date format changed
                    Dispatcher.InvokeAsync(UpdateClock, DispatcherPriority.Background);
                }
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Top center of the primary monitor's work area (so a top-docked taskbar is respected).</summary>
    private void PositionAtTopCenter()
    {
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, out NativeMethods.RECT work, 0))
            return;

        // The work area is in physical pixels; WPF positions windows in DIPs.
        Matrix fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(work.Left, work.Top));
        Point bottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));
        Left = Math.Round(topLeft.X + (bottomRight.X - topLeft.X - Width) / 2);
        Top = Math.Round(topLeft.Y);
    }

    // ═══════════════════════════════ State → presentation ═══════════════════════════════

    /// <summary>
    /// The single source of truth for what the island shows. Priority: expanded (the user is looking) →
    /// HUD (something just changed) → now playing → dot.
    /// </summary>
    private FrameworkElement? ChooseView()
    {
        if (_expanded)
            return _media.Current is null ? ExpandedIdleView : ExpandedMediaView;
        if (_hud == Hud.Volume)
            return VolumeHudView;
        if (_hud == Hud.Charging)
            return ChargingHudView;
        if (_showMediaPill && _media.Current is not null)
            return CompactMediaView;
        return null;
    }

    private void UpdatePresentation()
    {
        FrameworkElement? view = ChooseView();
        if (view != _activeView)
        {
            _activeView = view;
            MorphTo(view);
        }
        UpdateEqualizer();
        UpdateProgressTimer();
    }

    /// <summary>
    /// Animates the island to the size of <paramref name="view"/> (or the dot) and cross-fades the content.
    /// Animations start from the current animated value (HandoffBehavior.SnapshotAndReplace), so a morph that
    /// interrupts another one continues smoothly from wherever the island is.
    /// </summary>
    private void MorphTo(FrameworkElement? view)
    {
        double width = view?.Width ?? IslandConfig.DotSize;
        double height = view?.Height ?? IslandConfig.DotSize;

        // A spring when growing or switching pills; a plain ease into the dot, because a spring
        // would undershoot the tiny target size (and a negative size is invalid).
        bool toDot = view is null;
        IEasingFunction curve = toDot ? SettleCurve : SpringCurve;
        TimeSpan duration = toDot ? ShrinkToDotDuration : MorphDuration;

        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = curve });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = curve });

        foreach (FrameworkElement v in _views)
        {
            bool show = ReferenceEquals(v, view);
            v.IsHitTestVisible = show;

            // Outgoing content fades fast; incoming content waits until the shape has mostly formed.
            v.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 240 : 110))
            {
                BeginTime = show ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero,
                EasingFunction = FadeCurve,
            });

            if (show && v.RenderTransform is ScaleTransform scale)
            {
                var grow = new DoubleAnimation(0.9, 1, duration) { EasingFunction = SpringCurve };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the content to the island's rounded shape (one reused geometry, updated every animation frame).
        double radius = Math.Max(0, Math.Min(e.NewSize.Height / 2, IslandConfig.MaxCornerRadius - 1));
        _stageClip.Rect = new Rect(e.NewSize);
        _stageClip.RadiusX = radius;
        _stageClip.RadiusY = radius;
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;
        _expanded = expanded;

        if (expanded)
        {
            _hud = Hud.None; // the full card already shows everything a HUD would
            _hudTimer.Cancel();
            _trimTimer.Cancel();
        }
        else if (IslandConfig.TrimMemoryWhenIdle)
        {
            _trimTimer.Schedule(TimeSpan.FromSeconds(30), TrimMemory);
        }

        UpdatePresentation();
        if (expanded)
            UpdateProgress();
    }

    private void ShowHud(Hud hud, TimeSpan duration)
    {
        if (_expanded || _hiddenForFullscreen)
            return;
        _hud = hud;
        _hudTimer.Schedule(duration, () =>
        {
            _hud = Hud.None;
            UpdatePresentation();
        });
        UpdatePresentation();
    }

    /// <summary>The bars only animate while music plays and a media view is on screen. Otherwise no frames are rendered at all.</summary>
    private void UpdateEqualizer()
    {
        bool run = !_hiddenForFullscreen
                   && _media.Current is { IsPlaying: true }
                   && (_activeView == CompactMediaView || _activeView == ExpandedMediaView);
        if (run == _equalizerRunning || _equalizer is null)
            return;

        _equalizerRunning = run;
        if (run)
            _equalizer.Begin(this, isControllable: true);
        else
            _equalizer.Stop(this);
    }

    // ═══════════════════════════════ Mouse ═══════════════════════════════

    private void OnHitZoneMouseEnter(object sender, MouseEventArgs e)
    {
        if (_expanded)
            _hoverTimer.Cancel(); // came back during the collapse grace period
        else
            _hoverTimer.Schedule(IslandConfig.HoverExpandDelay, () => SetExpanded(true));
    }

    private void OnHitZoneMouseLeave(object sender, MouseEventArgs e)
    {
        if (_expanded)
            _hoverTimer.Schedule(IslandConfig.HoverCollapseDelay, () => SetExpanded(false));
        else
            _hoverTimer.Cancel(); // passed over the island without stopping
    }

    private void OnHitZoneLeftClick(object sender, MouseButtonEventArgs e)
    {
        // Click (or tap, on touch screens) toggles. Buttons inside the island handle their own clicks,
        // so this only fires for the island's background.
        _hoverTimer.Cancel();
        SetExpanded(!_expanded);
    }

    private void OnHitZoneRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowContextMenu();
    }

    /// <summary>A native Win32 menu: Windows 11 styling for free, and none of the focus quirks of WPF popups in a no-activate window.</summary>
    private void ShowContextMenu()
    {
        const int StartupItem = 1, ExitItem = 2;

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | (StartupManager.IsEnabled ? NativeMethods.MF_CHECKED : 0), StartupItem, "Start with Windows");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, ExitItem, "Exit Dynamic Island");

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            IntPtr previousForeground = NativeMethods.GetForegroundWindow();

            // A popup menu only closes properly on an outside click when its owner is in the foreground (KB135788).
            NativeMethods.SetForegroundWindow(_hwnd);
            int command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case StartupItem:
                    StartupManager.IsEnabled = !StartupManager.IsEnabled;
                    break;
                case ExitItem:
                    Close();
                    return;
            }

            // Give focus back to the app you were using (unless you already clicked somewhere else).
            if (NativeMethods.GetForegroundWindow() == _hwnd && previousForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(previousForeground);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    // ═══════════════════════════════ Media ═══════════════════════════════

    private void OnMediaChanged()
    {
        MediaInfo? info = _media.Current;
        if (info is not null)
            ApplyMedia(info);

        // The pill shows while playing. After a pause it lingers briefly (this also absorbs the paused
        // blip many players report between tracks), and it disappears at once when the player closes.
        if (info is null)
        {
            _pausedTimer.Cancel();
            _showMediaPill = false;
        }
        else if (info.IsPlaying)
        {
            _pausedTimer.Cancel();
            _showMediaPill = true;
        }
        else if (_showMediaPill && !_pausedTimer.IsPending)
        {
            _pausedTimer.Schedule(IslandConfig.PausedToDotDelay, () =>
            {
                _showMediaPill = false;
                UpdatePresentation();
            });
        }

        UpdatePresentation();
        if (_activeView == ExpandedMediaView)
            UpdateProgress();
    }

    private void ApplyMedia(MediaInfo info)
    {
        string title = info.Title.Length > 0 ? info.Title : info.AppName;
        CompactTitle.Text = title;
        ExpandedTitle.Text = title;
        ExpandedArtist.Text = info.Artist;
        ExpandedSource.Text = info.AppName;
        CompactArtBrush.ImageSource = info.Artwork;
        ExpandedArtBrush.ImageSource = info.Artwork;

        PlayIcon.Visibility = info.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = info.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.IsEnabled = info.CanPlayPause;
        PreviousButton.IsEnabled = info.CanGoPrevious;
        NextButton.IsEnabled = info.CanGoNext;

        // Tint the equalizer and progress bar with the cover's colour, with a smooth cross-fade between tracks.
        Color accent = info.ArtworkAccent ?? Colors.White;
        if (accent != _accentTarget)
        {
            _accentTarget = accent;
            _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(450)));
        }
    }

    private void UpdateProgressTimer()
    {
        bool run = _activeView == ExpandedMediaView && _media.Current is { IsPlaying: true };
        if (run && !_progressTimer.IsEnabled)
        {
            UpdateProgress();
            _progressTimer.Start();
        }
        else if (!run && _progressTimer.IsEnabled)
        {
            _progressTimer.Stop();
        }
    }

    private void OnProgressTick(object? sender, EventArgs e) => UpdateProgress();

    private void UpdateProgress()
    {
        if (!_media.TryGetTimeline(out TimeSpan position, out TimeSpan duration))
        {
            ProgressRow.Visibility = Visibility.Hidden; // this player does not report a timeline
            return;
        }

        ProgressRow.Visibility = Visibility.Visible;
        ProgressFill.Width = ProgressTrack.ActualWidth * Math.Clamp(position / duration, 0, 1);
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(duration - position);
    }

    private static string FormatTime(TimeSpan t)
        => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.SkipNextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.SkipPreviousAsync();

    private void OnOpenSpotifyClick(object sender, RoutedEventArgs e) => OpenSpotify();

    private void OnArtworkClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't let the click reach the island and collapse it
        if (_media.Current?.IsSpotify == true)
            OpenSpotify();
    }

    /// <summary>The spotify: URI launches Spotify, or brings it to the front if it is already running.</summary>
    private static void OpenSpotify()
    {
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not launch Spotify (is it installed?)", ex);
        }
    }

    // ═══════════════════════════════ Volume ═══════════════════════════════

    private void OnVolumeChanged(float level, bool muted)
    {
        int percent = (int)Math.Round(level * 100);
        VolumeText.Text = percent.ToString(CultureInfo.CurrentCulture);
        VolumeFill.Opacity = muted ? 0.35 : 1;
        VolumeFillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(level, TimeSpan.FromMilliseconds(140)) { EasingFunction = FadeCurve });

        // Speaker waves follow the level; a cross replaces them when muted.
        VolumeMuteCross.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeWave1.Visibility = !muted && percent > 0 ? Visibility.Visible : Visibility.Collapsed;
        VolumeWave2.Visibility = !muted && percent > 33 ? Visibility.Visible : Visibility.Collapsed;
        VolumeWave3.Visibility = !muted && percent > 66 ? Visibility.Visible : Visibility.Collapsed;

        ShowHud(Hud.Volume, IslandConfig.VolumeHudDuration);
    }

    // ═══════════════════════════════ Battery ═══════════════════════════════

    private void OnBatteryChanged(BatteryStatus previous, BatteryStatus current)
    {
        ApplyBattery(current);
        if (current.HasBattery && current.IsPluggedIn && !previous.IsPluggedIn)
            ShowHud(Hud.Charging, IslandConfig.ChargingHudDuration); // like iOS when you plug in
    }

    private void ApplyBattery(BatteryStatus battery)
    {
        Visibility visibility = battery.HasBattery ? Visibility.Visible : Visibility.Collapsed; // desktops: no battery UI
        MediaBatteryPanel.Visibility = visibility;
        IdleBatteryPanel.Visibility = visibility;
        if (!battery.HasBattery)
            return;

        string text = battery.Percent.ToString(CultureInfo.CurrentCulture) + "%";
        MediaBatteryText.Text = text;
        IdleBatteryText.Text = text;
        ChargingPercentText.Text = text;

        Brush fill = battery.IsCharging || battery.IsPluggedIn ? _greenBrush
                   : battery.Percent <= 20 ? _redBrush
                   : battery.IsSaverOn ? _yellowBrush
                   : Brushes.White;
        double level = battery.Percent / 100.0;
        foreach (var icon in new[] { MediaBatteryIcon, IdleBatteryIcon })
        {
            icon.Level = level;
            icon.Fill = fill;
            icon.IsCharging = battery.IsCharging;
        }
        ChargingBatteryIcon.Level = level;
    }

    // ═══════════════════════════════ Clock & weather ═══════════════════════════════

    private void OnMinuteTick(object? sender, EventArgs e)
    {
        UpdateClock();
        ScheduleNextMinuteTick();
        _power.Refresh(); // cheap safety net in case a power notification was ever missed

        if (_weather is not null && ++_minutesSinceWeather >= IslandConfig.WeatherRefreshInterval.TotalMinutes)
            _ = RefreshWeatherAsync();
    }

    /// <summary>Tick right after each minute boundary instead of polling every second.</summary>
    private void ScheduleNextMinuteTick()
    {
        DateTime now = DateTime.Now;
        _minuteTimer.Interval = TimeSpan.FromSeconds(60 - now.Second)
                                - TimeSpan.FromMilliseconds(now.Millisecond)
                                + TimeSpan.FromMilliseconds(50);
    }

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string monthDay = now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        string time = now.ToString("t", culture); // follows the 12/24-hour setting

        MediaClockText.Text = time;
        MediaDateText.Text = now.ToString("ddd", culture) + ", " + monthDay;
        IdleClockText.Text = time;
        IdleDateText.Text = now.ToString("dddd", culture) + ", " + monthDay;
    }

    private void OnResumedFromSleep()
    {
        UpdateClock();
        ScheduleNextMinuteTick();
        if (_weather is not null)
            _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_weather is null)
            return;
        _minutesSinceWeather = 0;

        WeatherInfo? weather = await _weather.GetCurrentAsync();
        string compact = weather is null ? "" : $"{weather.Glyph} {weather.Temperature.ToString(CultureInfo.CurrentCulture)}°";
        MediaWeatherText.Text = compact;
        IdleWeatherText.Text = weather is null ? "" : $"{compact}  {weather.Description}";

        Visibility visibility = weather is null ? Visibility.Collapsed : Visibility.Visible;
        MediaWeatherText.Visibility = visibility;
        IdleWeatherText.Visibility = visibility;
    }

    // ═══════════════════════════════ Fullscreen & memory ═══════════════════════════════

    private void OnFullscreenTick(object? sender, EventArgs e)
    {
        bool fullscreen = NativeMethods.IsFullscreenAppInForeground(_hwnd);
        if (fullscreen == _hiddenForFullscreen)
            return;

        _hiddenForFullscreen = fullscreen;
        if (fullscreen)
        {
            _hoverTimer.Cancel();
            _hudTimer.Cancel();
            _expanded = false;
            _hud = Hud.None;
        }
        else
        {
            // Fullscreen apps can push other topmost windows down; take the top spot back.
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Fully transparent and not hit-testable = invisible and 100 % click-through.
        HitZone.IsHitTestVisible = !fullscreen;
        HitZone.BeginAnimation(OpacityProperty, new DoubleAnimation(fullscreen ? 0 : 1, TimeSpan.FromMilliseconds(fullscreen ? 150 : 300)));
        UpdatePresentation();
    }

    /// <summary>
    /// Collects the garbage left over from startup and the last interaction, then lets Windows take back the
    /// physical pages we are not using. Pages come back on demand (a cheap soft fault), so nothing breaks;
    /// the app simply stops holding RAM it does not need while it sits idle.
    /// </summary>
    private static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
