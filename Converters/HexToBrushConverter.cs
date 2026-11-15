using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LandingRecorder.Converters;

/// <summary>Maps a "#RRGGBB"/"#AARRGGBB" string to a brush, keeping System.Windows.Media out of the ViewModels.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            try
            {
                if (ColorConverter.ConvertFromString(text.Trim()) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch (FormatException)
            {
                // Fall through: invalid (possibly half-typed) color.
            }
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
