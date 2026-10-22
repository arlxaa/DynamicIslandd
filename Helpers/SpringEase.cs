using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland.Helpers;

/// <summary>
/// Damped-spring easing: the "fast, slightly bouncy, then settled" motion of the iPhone's Dynamic Island.
/// <code>spring(t) = 1 − e^(−ζωt) · (cos(ω_d·t) + (ζω / ω_d) · sin(ω_d·t)),   ω_d = ω·√(1 − ζ²)</code>
/// Plug it into any WPF animation: <c>new DoubleAnimation(to, duration) { EasingFunction = new SpringEase() }</c>.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingRatioProperty = DependencyProperty.Register(
        nameof(DampingRatio), typeof(double), typeof(SpringEase), new PropertyMetadata(0.72));

    public static readonly DependencyProperty StiffnessProperty = DependencyProperty.Register(
        nameof(Stiffness), typeof(double), typeof(SpringEase), new PropertyMetadata(8.5));

    /// <summary>EaseInCore below already returns the whole curve, so WPF must not mirror it.</summary>
    public SpringEase() => EasingMode = EasingMode.EaseIn;

    /// <summary>ζ. Below 1 the motion overshoots a little (0.72 ≈ 4 %); values near 1 settle without overshoot.</summary>
    public double DampingRatio
    {
        get => (double)GetValue(DampingRatioProperty);
        set => SetValue(DampingRatioProperty, value);
    }

    /// <summary>ω, the natural frequency per animation duration. Higher = snappier, settles earlier.</summary>
    public double Stiffness
    {
        get => (double)GetValue(StiffnessProperty);
        set => SetValue(StiffnessProperty, value);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        // Dividing by spring(1) makes the curve end exactly at 1, so there is no tiny snap on the last frame.
        return Spring(normalizedTime) / Spring(1.0);
    }

    private double Spring(double t)
    {
        double zeta = Math.Clamp(DampingRatio, 0.05, 0.999);
        double omega = Stiffness;
        double omegaD = omega * Math.Sqrt(1 - zeta * zeta);
        double decay = Math.Exp(-zeta * omega * t);
        return 1 - decay * (Math.Cos(omegaD * t) + zeta * omega / omegaD * Math.Sin(omegaD * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
