using System.Globalization;
using System.Windows.Data;

namespace ClaudeUsageMonitor.ViewModels;

public static class Converters
{
    public static IValueConverter Not { get; } = new NotConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
