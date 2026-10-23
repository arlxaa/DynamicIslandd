using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>
/// The pill style, chosen in the island's menu or the tray icon's. The full pill shows the cover, the song title
/// and the visualizer; the minimal one, like the iPhone, just the cover and the visualizer (and a timer just its
/// ring and countdown), so the island stays small. The choice is saved with the rest of the state.
/// </summary>
public partial class MainWindow
{
    private bool _minimalPill;

    private void OnPillStyleClick(object sender, RoutedEventArgs e) => SetMinimalPill(sender == MinimalPillButton);

    private void SetMinimalPill(bool minimal)
    {
        if (_minimalPill == minimal)
            return;
        _minimalPill = minimal;
        ApplyPillStyle();
        SaveState();
    }

    /// <summary>Lays out the pills for the current style, and for a timer running next to the music.</summary>
    private void ApplyPillStyle()
    {
        bool minimal = _minimalPill;
        CompactTitle.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        CompactTimerLabel.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        CompactEqualizer.Margin = new Thickness(0, 0, minimal ? 10 : 14, 0);

        double mediaWidth = !minimal ? IslandConfig.CompactWidth
                          : _timer.IsActive ? IslandConfig.MinimalMediaTimerWidth
                          : IslandConfig.MinimalMediaWidth;
        double timerWidth = minimal ? IslandConfig.MinimalTimerWidth : IslandConfig.CompactTimerWidth;
        bool resize = (_activeView == CompactMediaView && CompactMediaView.Width != mediaWidth)
                   || (_activeView == CompactTimerView && CompactTimerView.Width != timerWidth);
        CompactMediaView.Width = mediaWidth;
        CompactTimerView.Width = timerWidth;

        // In the menu, the thumb slides under the chosen drawing, which is shown at full strength.
        PillStyleThumbOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(minimal ? FullPillButton.Width : 0, SlideDuration) { EasingFunction = SpringCurve });
        (minimal ? MinimalPillButton : FullPillButton).Opacity = 1;
        (minimal ? FullPillButton : MinimalPillButton).ClearValue(OpacityProperty);

        if (resize && _activeView is not null)
        {
            // The pill is on screen (say, a timer just started next to the music): spring to its new width.
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(_activeView.Width, MorphDuration) { EasingFunction = SpringCurve });
            UpdateMakeWay();
        }
    }
}
