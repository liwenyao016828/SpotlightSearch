using System.Globalization;
using System.Windows.Data;

namespace SpotlightSearch.Controls;

/// <summary>1..9 badges for the first nine rows, exactly like the original list.</summary>
public sealed class ResultHotkeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int index = value is int number ? number : -1;
        return index is >= 0 and < 9 ? (index + 1).ToString(culture) : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
