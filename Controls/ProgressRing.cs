using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Controls;

/// <summary>
/// The timer's progress ring: a faint full circle with a bright arc on top that starts at 12 o'clock and runs
/// clockwise. Drawn directly in <see cref="OnRender"/> like <see cref="BatteryIcon"/>, so it has no child elements
/// and redraws only when <see cref="Progress"/> changes (once a second while a timer runs).
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x9F, 0x0A))), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the ring is lit, from 0 to 1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double thickness = Thickness;
        double radius = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2;
        if (radius <= 0)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        double progress = Math.Clamp(Progress, 0, 1);
        if (progress < 0.001)
            return;

        var pen = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (progress > 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        double angle = progress * 2 * Math.PI;
        var arc = new StreamGeometry();
        using (StreamGeometryContext g = arc.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - radius), isFilled: false, isClosed: false);
            g.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                    new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise,
                    isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
