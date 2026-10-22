using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
/// morphs between states.
/// <list type="bullet">
/// <item><b>Dot</b>: idle, when nothing is going on.</item>
/// <item><b>Compact</b>: a pill with a live activity (now playing, a running timer, a volume change, the charger being plugged in).</item>
/// <item><b>Expanded</b>: on hover or click, a card with three pages (now playing, timer, to-do) under a shared header.</item>
/// <item><b>Alert</b> and <b>menu</b>: "Timer done", and the menu you get with a right-click.</item>
/// </list>
/// Everything is event-driven. The only always-on timer fires once per minute for the clock.
/// This file holds the state machine and the window plumbing; the timer and the to-do list live in
/// MainWindow.Timer.cs and MainWindow.Todos.cs.
/// </summary>
public partial class MainWindow : Window
{
    private enum Hud { None, Volume, Charging }

    /// <summary>The expanded card's pages, in tab order.</summary>
    private enum Page { Media, Timer, Todo }

    // ─────────────── Animation curves (frozen: shared by every animation at no cost) ───────────────

    private static readonly IEasingFunction SpringCurve = Frozen(new SpringEase { DampingRatio = 0.72, Stiffness = 8.5 });
    private static readonly IEasingFunction SettleCurve = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction FadeCurve = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan ShrinkToDotDuration = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(420);  // tab thumb, switch knob

    /// <summary>Width of one page tab in the header (see the TabButton style).</summary>
    private const double TabWidth = 32;

    /// <summary>Hovering again this soon after the island collapsed reopens the page you were on.</summary>
    private static readonly TimeSpan StickyPageWindow = TimeSpan.FromSeconds(10);

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

    private bool _expanded;                // the user is hovering over or has clicked the island
    private Page _page = Page.Media;       // the expanded card's current page
    private bool _menuOpen;                // the right-click menu is up (it counts as expanded)
    private Hud _hud = Hud.None;           // a transient announcement is up
    private bool _showMediaPill;           // music is playing (or was paused only moments ago)
    private bool _fullscreenInFront;       // a fullscreen app is in front...
    private bool _hiddenForFullscreen;     // ...so the island stays out of the way (unless a timer is ringing)
    private FrameworkElement? _activeView; // null = dot
    private readonly FrameworkElement[] _views;
    private readonly FrameworkElement[] _pageViews;  // the expanded pages, which share the header
    private DateTime _collapsedAt = DateTime.MinValue;

    // ─────────────── Misc ───────────────

    private readonly SolidColorBrush _accentBrush = new(Colors.White);
    private Color _accentTarget = Colors.White;
    private readonly RectangleGeometry _stageClip = new();
    private readonly Brush _greenBrush, _redBrush, _yellowBrush, _orangeBrush, _switchOffBrush;
    private readonly Button[] _tabs;
    private bool _headerShown;
    private bool _headerClockShown = true;
    private Storyboard? _equalizer;
    private bool _equalizerRunning;
    private int _minutesSinceWeather;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        _views = new FrameworkElement[]
        {
            CompactMediaView, CompactTimerView, VolumeHudView, ChargingHudView, TimerAlertView, MenuView,
            ExpandedMediaView, ExpandedIdleView, TimerSetupView, TimerRunningView, TodoView,
        };
        _pageViews = new FrameworkElement[] { ExpandedMediaView, ExpandedIdleView, TimerSetupView, TimerRunningView, TodoView };
        _tabs = new[] { MediaTab, TimerTab, TodoTab };
        _greenBrush = (Brush)FindResource("GreenBrush");
        _redBrush = (Brush)FindResource("RedBrush");
        _yellowBrush = (Brush)FindResource("YellowBrush");
        _orangeBrush = (Brush)FindResource("OrangeBrush");
        _switchOffBrush = (Brush)FindResource("SwitchOffBrush");

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

        // What the island remembers between runs: the to-do list and a running timer.
        SavedState saved = StateStore.Load();
        InitializeTodos(saved);
        InitializeTimer(saved);
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : "v" + version.ToString(3);

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
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

    // ═══════════════════════════════ Lifecycle ═══════════════════════════════

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not in Alt+Tab or the taskbar, and shown on every virtual desktop.
        // No-activate: clicking the island never steals keyboard focus from the app you are working in
        // (the to-do box asks for focus explicitly, and only while you type).
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
        UpdateTabs();
        UpdatePresentation(); // a timer restored from the last run shows its pill right away
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
        SaveState();

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
        DisposeTimer();
        DisposeTodos();

        _media.Changed -= OnMediaChanged;
        _media.Dispose();
        _volume.VolumeChanged -= OnVolumeChanged;
        _volume.Dispose();
        _power.Changed -= OnBatteryChanged;
        _power.Dispose();
        _weather?.Dispose();

        Stage.SizeChanged -= OnStageSizeChanged;
        Deactivated -= OnDeactivated;
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

    /// <summary>Writes the to-dos and the timer to disk. Called after every change you make.</summary>
    private void SaveState() => StateStore.Save(new SavedState
    {
        Todos = _todos.Select(todo => new SavedTodo(todo.Text, todo.IsDone)).ToList(),
        Timer = _timer.Save(),
        LastTimerMinutes = _timerMinutes,
    });

    // ═══════════════════════════════ State → presentation ═══════════════════════════════

    /// <summary>
    /// The single source of truth for what the island shows. Priority: a ringing timer (it needs you) →
    /// the menu → expanded (the user is looking) → HUD (something just changed) → now playing → timer → dot.
    /// </summary>
    private FrameworkElement? ChooseView()
    {
        if (_timer.Phase == TimerPhase.Ringing)
            return TimerAlertView;
        if (_menuOpen)
            return MenuView;
        if (_expanded)
        {
            return _page switch
            {
                Page.Timer => _timer.IsActive ? TimerRunningView : TimerSetupView,
                Page.Todo => TodoView,
                _ => _media.Current is null ? ExpandedIdleView : ExpandedMediaView,
            };
        }
        if (_hud == Hud.Volume)
            return VolumeHudView;
        if (_hud == Hud.Charging)
            return ChargingHudView;
        if (_showMediaPill && _media.Current is not null)
            return CompactMediaView; // with a running timer, the pill shows its countdown as well
        if (_timer.IsActive)
            return CompactTimerView;
        return null;
    }

    private void UpdatePresentation()
    {
        FrameworkElement? view = ChooseView();
        if (view != _activeView)
        {
            FrameworkElement? previous = _activeView;
            _activeView = view;
            MorphTo(previous, view);
        }
        UpdateEqualizer();
        UpdateProgressTimer();
    }

    /// <summary>
    /// Animates the island to the size of <paramref name="view"/> (or the dot) and cross-fades the content.
    /// Animations start from the current animated value (HandoffBehavior.SnapshotAndReplace), so a morph that
    /// interrupts another one continues smoothly from wherever the island is.
    /// </summary>
    private void MorphTo(FrameworkElement? previous, FrameworkElement? view)
    {
        double width = view?.Width ?? IslandConfig.DotSize;
        double height = view == TodoView ? PrepareTodoView() : view?.Height ?? IslandConfig.DotSize;

        // A spring when growing or switching shapes; a plain ease into the dot, because a spring
        // would undershoot the tiny target size (and a negative size is invalid).
        bool toDot = view is null;
        IEasingFunction curve = toDot ? SettleCurve : SpringCurve;
        TimeSpan duration = toDot ? ShrinkToDotDuration : MorphDuration;

        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = curve });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = curve });

        // Switching pages keeps the header in place and only nudges the new content, instead of popping it in.
        bool pageSwitch = IsPage(previous) && IsPage(view);
        foreach (FrameworkElement v in _views)
        {
            bool show = ReferenceEquals(v, view);
            v.IsHitTestVisible = show;
            v.BeginAnimation(OpacityProperty, Fade(show));

            if (show && v.RenderTransform is ScaleTransform scale)
            {
                var grow = new DoubleAnimation(pageSwitch ? 0.96 : 0.9, 1, duration) { EasingFunction = SpringCurve };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        ShowHeader(IsPage(view), showClock: view != ExpandedIdleView);
    }

    /// <summary>Outgoing content fades fast; incoming content waits until the shape has mostly formed.</summary>
    private static DoubleAnimation Fade(bool show) => new(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 240 : 110))
    {
        BeginTime = show ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero,
        EasingFunction = FadeCurve,
    };

    private bool IsPage(FrameworkElement? view) => view is not null && Array.IndexOf(_pageViews, view) >= 0;

    /// <summary>
    /// The header (clock, tabs, battery) is shared by the expanded pages, so it only fades when the card opens or
    /// closes. Its clock steps aside on the "nothing playing" page, which shows a big clock instead.
    /// </summary>
    private void ShowHeader(bool show, bool showClock)
    {
        if (show != _headerShown)
        {
            _headerShown = show;
            PageHeader.IsHitTestVisible = show;
            PageHeader.BeginAnimation(OpacityProperty, Fade(show));
        }
        if (showClock != _headerClockShown)
        {
            _headerClockShown = showClock;
            HeaderClockPanel.BeginAnimation(OpacityProperty, Fade(showClock));
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
            SetPage(PageForExpansion(), animate: false);
        }
        else
        {
            _menuOpen = false;
            EndTyping(restoreFocus: true);
            _collapsedAt = DateTime.UtcNow;
            if (IslandConfig.TrimMemoryWhenIdle)
                _trimTimer.Schedule(TimeSpan.FromSeconds(30), TrimMemory);
        }

        UpdatePresentation();
        if (expanded)
            UpdateProgress();
    }

    /// <summary>
    /// The page to open: the one you were just on if you come straight back; otherwise the activity the pill is
    /// showing (the timer, or now playing); otherwise the page you used last.
    /// </summary>
    private Page PageForExpansion()
    {
        if (DateTime.UtcNow - _collapsedAt < StickyPageWindow)
            return _page;
        if (_activeView == CompactTimerView)
            return Page.Timer;
        if (_activeView == CompactMediaView)
            return Page.Media;
        return _page;
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
        => SetPage(sender == TimerTab ? Page.Timer : sender == TodoTab ? Page.Todo : Page.Media, animate: true);

    private void SetPage(Page page, bool animate)
    {
        if (page != _page)
        {
            if (_page == Page.Todo)
                EndTyping(restoreFocus: true);
            _page = page;
            UpdateTabs();
        }
        if (page == Page.Todo)
            SortTodos(); // finished to-dos sink below the open ones whenever the list comes into view
        if (animate)
            UpdatePresentation();
    }

    /// <summary>Slides the tab thumb to the current page and highlights its icon.</summary>
    private void UpdateTabs()
    {
        int selected = (int)_page;
        TabThumbOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(selected * TabWidth, SlideDuration) { EasingFunction = SpringCurve });
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (i == selected)
                _tabs[i].Opacity = 1;
            else
                _tabs[i].ClearValue(OpacityProperty); // back to the style's dimmed / hover values
        }
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

    /// <summary>
    /// The bars only animate while music plays and they are on screen (in the compact pill a running timer's
    /// countdown takes their place). Otherwise no frames are rendered at all.
    /// </summary>
    private void UpdateEqualizer()
    {
        bool run = !_hiddenForFullscreen
                   && _media.Current is { IsPlaying: true }
                   && (_activeView == ExpandedMediaView || (_activeView == CompactMediaView && !_timer.IsActive));
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
        if (_expanded && !_typing)
            _hoverTimer.Schedule(IslandConfig.HoverCollapseDelay, () => SetExpanded(false));
        else if (!_expanded)
            _hoverTimer.Cancel(); // passed over the island without stopping
    }

    private void OnHitZoneLeftClick(object sender, MouseButtonEventArgs e)
    {
        // Buttons inside the island handle their own clicks, so this only fires for its background.
        _hoverTimer.Cancel();
        if (_timer.Phase == TimerPhase.Ringing)
            _timer.Stop();              // a click on the alert dismisses it
        else if (_menuOpen)
            CloseMenu();
        else if (!_expanded)
            SetExpanded(true);          // a click (or a tap on a touch screen) opens it; moving away closes it
    }

    private void OnHitZoneRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // No menu over a ringing timer (it would only appear after you stop it) or inside the to-do box.
        if (_timer.Phase != TimerPhase.Ringing && !TodoInput.IsMouseOver)
            OpenMenu();
    }

    // ═══════════════════════════════ Menu ═══════════════════════════════

    /// <summary>The right-click menu lives inside the island itself, in the same type and style as everything else.</summary>
    private void OpenMenu()
    {
        _hoverTimer.Cancel();
        EndTyping(restoreFocus: true);
        SetStartupSwitch(StartupManager.IsEnabled, animate: false);
        _menuOpen = true;
        if (_expanded)
            UpdatePresentation();
        else
            SetExpanded(true); // collapses again like the card when the pointer leaves
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        UpdatePresentation();
    }

    private void OnStartupToggleClick(object sender, RoutedEventArgs e)
    {
        StartupManager.IsEnabled = !StartupManager.IsEnabled;
        SetStartupSwitch(StartupManager.IsEnabled, animate: true); // read back: shows the truth if the change failed
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => Close();

    private void SetStartupSwitch(bool on, bool animate)
    {
        StartupSwitch.Background = on ? _greenBrush : _switchOffBrush;
        double x = on ? StartupSwitch.Width - StartupSwitch.Height : 0; // knob travel = track width − track height
        if (animate)
        {
            StartupSwitchKnob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, SlideDuration) { EasingFunction = SpringCurve });
        }
        else
        {
            StartupSwitchKnob.BeginAnimation(TranslateTransform.XProperty, null);
            StartupSwitchKnob.X = x;
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

    /// <summary>"4:59" or, from an hour up, "1:04:59" (track positions and the timer).</summary>
    private static string FormatTime(TimeSpan t)
        => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.SkipNextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.SkipPreviousAsync();

    private void OnOpenSpotifyClick(object sender, RoutedEventArgs e) => OpenSpotify();

    private void OnArtworkClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // the click is the artwork's, not the island's
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
        HeaderBatteryPanel.Visibility = battery.HasBattery ? Visibility.Visible : Visibility.Collapsed; // desktops: no battery UI
        if (!battery.HasBattery)
            return;

        string text = battery.Percent.ToString(CultureInfo.CurrentCulture) + "%";
        HeaderBatteryText.Text = text;
        ChargingPercentText.Text = text;

        double level = battery.Percent / 100.0;
        HeaderBatteryIcon.Level = level;
        HeaderBatteryIcon.IsCharging = battery.IsCharging;
        HeaderBatteryIcon.Fill = battery.IsCharging || battery.IsPluggedIn ? _greenBrush
                               : battery.Percent <= 20 ? _redBrush
                               : battery.IsSaverOn ? _yellowBrush
                               : Brushes.White;
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
        string shortTime = culture.DateTimeFormat.ShortTimePattern; // follows the 12/24-hour setting

        HeaderClockText.Text = now.ToString(shortTime, culture);
        HeaderDateText.Text = now.ToString("ddd d", culture);
        // The big clock leaves out AM/PM, like the iPhone's lock screen.
        IdleClockText.Text = now.ToString(shortTime.Replace("tt", "").Replace("t", "").Trim(), culture);
        IdleDateText.Text = now.ToString("dddd", culture) + ", " + now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
    }

    private void OnResumedFromSleep()
    {
        UpdateClock();
        ScheduleNextMinuteTick();
        _timer.Refresh(); // a timer that ran out while the PC slept rings now
        if (_weather is not null)
            _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_weather is null)
            return;
        _minutesSinceWeather = 0;

        WeatherInfo? weather = await _weather.GetCurrentAsync();
        Visibility visibility = weather is null ? Visibility.Collapsed : Visibility.Visible;
        HeaderWeatherPanel.Visibility = visibility;
        IdleWeatherText.Visibility = visibility;
        if (weather is null)
            return;

        HeaderWeatherIcon.Source = (ImageSource)FindResource("Weather." + weather.Kind);
        HeaderWeatherText.Text = weather.Temperature.ToString(CultureInfo.CurrentCulture) + "°";
        IdleWeatherText.Text = weather.Description;
    }

    // ═══════════════════════════════ Fullscreen & memory ═══════════════════════════════

    private void OnFullscreenTick(object? sender, EventArgs e)
    {
        _fullscreenInFront = NativeMethods.IsFullscreenAppInForeground(_hwnd);
        UpdateFullscreenHiding();
    }

    /// <summary>Hides the island while a fullscreen app is in front, except when a finished timer needs your attention.</summary>
    private void UpdateFullscreenHiding()
    {
        bool hide = _fullscreenInFront && _timer.Phase != TimerPhase.Ringing;
        if (hide == _hiddenForFullscreen)
            return;

        _hiddenForFullscreen = hide;
        if (hide)
        {
            _hoverTimer.Cancel();
            _hudTimer.Cancel();
            _expanded = false;
            _menuOpen = false;
            _hud = Hud.None;
        }
        else
        {
            // Fullscreen apps can push other topmost windows down; take the top spot back.
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Fully transparent and not hit-testable = invisible and 100 % click-through.
        HitZone.IsHitTestVisible = !hide;
        HitZone.BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(hide ? 150 : 300)));
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
