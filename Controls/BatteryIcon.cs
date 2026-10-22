using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Controls;

/// <summary>
/// iOS-style battery glyph drawn directly in <see cref="OnRender"/>: no child elements and no template,
/// so each instance costs almost no memory and redraws only when a property changes.
/// </summary>
public sealed class BatteryIcon : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush OutlineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen OutlinePen = Frozen(new Pen(OutlineBrush, 1));
    private static readonly Geometry Bolt = Frozen(Geometry.Parse("M 6.2,0 L 0.4,8 H 4.6 L 3.6,14 L 9.6,5.8 H 5.4 Z"));

    /// <summary>Charge level from 0 to 1.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 5)
            return;

        const double nubWidth = 1.6;
        // Half-pixel offsets keep the 1 px outline crisp.
        var body = new Rect(0.5, 0.5, w - nubWidth - 1.5, h - 1);
        double radius = Math.Min(3.2, h / 3.5);
        dc.DrawRoundedRectangle(null, OutlinePen, body, radius, radius);
        dc.DrawRoundedRectangle(OutlineBrush, null, new Rect(body.Right + 0.8, h * 0.33, nubWidth, h * 0.34), 0.8, 0.8);

        double level = Math.Clamp(Level, 0, 1);
        var cell = new Rect(body.X + 1.5, body.Y + 1.5, Math.Max(0, (body.Width - 3) * level), body.Height - 3);
        double cellRadius = Math.Max(0, radius - 1.3);
        if (cell.Width > 0.3)
            dc.DrawRoundedRectangle(Fill, null, cell, cellRadius, cellRadius);

        if (IsCharging)
        {
            // Bolt centred in the body: black on a well-filled cell, white on an emptier one.
            Rect bounds = Bolt.Bounds;
            double scale = (body.Height - 2) / bounds.Height;
            dc.PushTransform(new TranslateTransform(body.X + (body.Width - bounds.Width * scale) / 2, body.Y + 1));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(level > 0.55 ? Brushes.Black : Brushes.White, null, Bolt);
            dc.Pop();
            dc.Pop();
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
