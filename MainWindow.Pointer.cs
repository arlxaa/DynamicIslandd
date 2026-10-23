using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// "Make way": the small island (the dot or a pill) sits right on top of other windows' tabs and title bars, so it
/// must never get in their way.
/// <list type="bullet">
/// <item>When the pointer arrives on it, the island turns see-through and lets clicks through, so you can see and
/// click the browser tab (or whatever) underneath.</item>
/// <item>If you rest the pointer there without clicking, it fades back in and opens (IslandConfig.RestToOpenDelay).</item>
/// <item>After a click it stays out of the way until the pointer leaves, so closing several tabs in a row just works.</item>
/// </list>
/// The window notices the pointer arriving like any window would; from then on it is click-through and gets no
/// mouse input, so <see cref="PointerWatcher"/> follows the pointer until it leaves. With
/// IslandConfig.ClickThroughWhenCollapsed off, none of this happens and the classic hover-to-open applies.
/// </summary>
public partial class MainWindow
{
    /// <summary>How visible the island stays while it makes way.</summary>
    private const double SeeThroughOpacity = 0.15;

    private static readonly TimeSpan SeeThroughFade = TimeSpan.FromMilliseconds(120);

    /// <summary>A pause shorter than this changes nothing; after it, the island starts fading back in to open.</summary>
    private static readonly TimeSpan RestRampDelay = TimeSpan.FromMilliseconds(200);

    private readonly OneShotTimer _restTimer = new();
    private PointerWatcher? _pointer;   // null in classic mode
    private bool _makingWay;            // the pointer is on the small island, which is see-through and click-through
    private bool _clickedThrough;       // a click went to the window underneath: don't open until the pointer leaves
    private bool _fadingBackIn;         // resting: fading back in, about to open

    private void InitializePointer()
    {
        if (!IslandConfig.ClickThroughWhenCollapsed)
            return;
        _pointer = new PointerWatcher(Dispatcher);
        _pointer.Left += OnPointerLeft;
        _pointer.Pressed += OnPointerPressed;
        _pointer.Moved += OnPointerMoved;
    }

    private void DisposePointer()
    {
        _restTimer.Dispose();
        if (_pointer is null)
            return;
        _pointer.Left -= OnPointerLeft;
        _pointer.Pressed -= OnPointerPressed;
        _pointer.Moved -= OnPointerMoved;
        _pointer.Dispose();
    }

    /// <summary>The dot or a pill is showing (not the card, the menu or the alert), and the island isn't hidden.</summary>
    private bool IsSmall => !_expanded && !_menuOpen && !_hidden && _timer.Phase != TimerPhase.Ringing;

    /// <summary>The pointer has just arrived on the small island.</summary>
    private void MakeWay()
    {
        if (_makingWay || _pointer is null)
            return;
        _makingWay = true;
        _clickedThrough = false;
        _hoverTimer.Cancel();

        SetClickThrough(true);
        Int32Rect area = SmallIslandArea();
        _pointer.Start(area.X, area.Y, area.X + area.Width, area.Y + area.Height);
        FadeIsland(SeeThroughOpacity, SeeThroughFade);
        WaitForRest();
    }

    /// <summary>Back to normal: visible, and catching the mouse so it notices the pointer arriving again.</summary>
    private void StopMakingWay()
    {
        if (!_makingWay)
            return;
        _makingWay = false;
        _fadingBackIn = false;
        _restTimer.Cancel();
        _pointer?.Stop();
        SetClickThrough(false);
        FadeIsland(1, TimeSpan.FromMilliseconds(200));
    }

    private void OnPointerLeft() => StopMakingWay();

    private void OnPointerPressed()
    {
        if (!_makingWay)
            return;
        _clickedThrough = true;
        _restTimer.Cancel();
        if (_fadingBackIn)
        {
            _fadingBackIn = false;
            FadeIsland(SeeThroughOpacity, SeeThroughFade);
        }
    }

    private void OnPointerMoved()
    {
        if (!_makingWay || _clickedThrough)
            return;
        if (_fadingBackIn)
        {
            _fadingBackIn = false; // moving again: not resting after all
            FadeIsland(SeeThroughOpacity, SeeThroughFade);
        }
        WaitForRest();
    }

    /// <summary>(Re)starts the rest countdown: after a short pause the island fades back in, then opens.</summary>
    private void WaitForRest()
    {
        _restTimer.Schedule(RestRampDelay, () =>
        {
            TimeSpan fadeIn = IslandConfig.RestToOpenDelay - RestRampDelay;
            if (fadeIn < TimeSpan.FromMilliseconds(50))
                fadeIn = TimeSpan.FromMilliseconds(50);
            _fadingBackIn = true;
            FadeIsland(1, fadeIn);
            _restTimer.Schedule(fadeIn, OpenAfterRest);
        });
    }

    private void OpenAfterRest()
    {
        StopMakingWay();
        SetExpanded(true);

        // The pointer is on the island, but the window hasn't seen it arrive (it was click-through). Tell it
        // where the pointer is, so that moving away collapses the island as usual.
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT p) && NativeMethods.ScreenToClient(_hwnd, ref p))
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
    }

    /// <summary>
    /// The island just shrank under the pointer (an alert was dismissed, typing ended, ...). The window won't see the
    /// pointer arrive, because it is already there, so make way now, without reopening until the pointer leaves.
    /// </summary>
    private void MakeWayIfPointerIsOver()
    {
        if (_pointer is null || _makingWay || !IsSmall || !NativeMethods.GetCursorPos(out NativeMethods.POINT p))
            return;
        Int32Rect area = SmallIslandArea();
        if (p.X < area.X || p.Y < area.Y || p.X >= area.X + area.Width || p.Y >= area.Y + area.Height)
            return;
        MakeWay();
        _clickedThrough = true;
        _restTimer.Cancel();
    }

    /// <summary>Keeps the watched area in step when the small island changes shape, or stops when it opens or hides.</summary>
    private void UpdateMakeWay()
    {
        if (!_makingWay)
            return;
        if (!IsSmall)
        {
            StopMakingWay();
            return;
        }
        Int32Rect area = SmallIslandArea();
        _pointer?.UpdateArea(area.X, area.Y, area.X + area.Width, area.Y + area.Height);
    }

    /// <summary>
    /// The small island's shape plus its hover margin, up to the top of the screen, in physical pixels. Computed
    /// from the target size, so it is right even while the island is still morphing.
    /// </summary>
    private Int32Rect SmallIslandArea()
    {
        double width = (_activeView?.Width ?? IslandConfig.DotSize) + 2 * IslandConfig.HoverPadding;
        double height = IslandConfig.TopMargin + (_activeView?.Height ?? IslandConfig.DotSize) + IslandConfig.HoverPadding;
        var area = new Rect(Left + (ActualWidth - width) / 2, Top, width, height);
        area.Transform(PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity);
        return new Int32Rect((int)Math.Floor(area.X), (int)Math.Floor(area.Y), (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height));
    }

    /// <summary>WS_EX_TRANSPARENT on a layered window: the mouse goes straight through to the window underneath.</summary>
    private void SetClickThrough(bool on)
    {
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        int wanted = on ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~NativeMethods.WS_EX_TRANSPARENT;
        if (wanted != style)
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, wanted);
    }

    private void FadeIsland(double to, TimeSpan duration)
        => Island.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration) { EasingFunction = FadeCurve });
}
