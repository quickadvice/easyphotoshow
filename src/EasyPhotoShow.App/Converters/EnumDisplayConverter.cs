using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace EasyPhotoShow.App.Converters;

// Splits PascalCase enum values into spaced display strings: BestMix → "Best Mix",
// KeepFolderOrder → "Keep Folder Order", Smooth → "Smooth". Keeps single-word values
// (Fade, Dissolve, None) untouched.
public sealed class EnumDisplayConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var name = value.ToString();
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
