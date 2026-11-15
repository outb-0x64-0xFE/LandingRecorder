using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LandingRecorder.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Brushes.LimeGreen : Brushes.IndianRed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
