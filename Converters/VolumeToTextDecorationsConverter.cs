using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LandingRecorder.Converters;

public sealed class VolumeToTextDecorationsConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double volume && volume <= 0 ? TextDecorations.Strikethrough : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
