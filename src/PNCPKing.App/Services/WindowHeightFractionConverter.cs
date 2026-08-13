using System.Globalization;
using System.Windows.Data;

namespace PNCPKing.App.Services;

public sealed class WindowHeightFractionConverter : IValueConverter
{
    public double Fraction { get; set; } = 0.42;
    public double Minimum { get; set; } = 180;
    public double Maximum { get; set; } = 400;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var height = value is double actual && double.IsFinite(actual) ? actual : Minimum;
        return Math.Clamp(height * Fraction, Minimum, Maximum);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
