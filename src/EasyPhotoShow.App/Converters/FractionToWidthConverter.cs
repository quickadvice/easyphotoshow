using System.Globalization;
using System.Windows.Data;

namespace EasyPhotoShow.App.Converters;

// Multiplies a 0..1 fraction by the container's ActualWidth to produce an explicit pixel
// width for a fill element. Used instead of WPF's PART_Indicator machinery, which silently
// no-ops if PART_Track is missing.
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        double fraction = values[0] is double f && !double.IsNaN(f) ? f : 0.0;
        double available = values[1] is double w && !double.IsNaN(w) ? w : 0.0;
        if (available <= 0) return 0.0;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return fraction * available;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
