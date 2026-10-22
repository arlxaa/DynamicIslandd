using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicIsland.Helpers;

/// <summary>
/// Height → CornerRadius. Half the height (a perfect circle or pill) while the island is small, capped at
/// <see cref="IslandConfig.MaxCornerRadius"/> for the expanded card. Bound to the animated Height, so the
/// corners morph continuously during every transition.
/// </summary>
public sealed class PillCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double height && double.IsFinite(height)
            ? new CornerRadius(Math.Min(height / 2, IslandConfig.MaxCornerRadius))
            : new CornerRadius(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
