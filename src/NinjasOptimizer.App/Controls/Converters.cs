using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NinjasOptimizer.Core.Optimization;

namespace NinjasOptimizer.App.Controls;

public sealed class ProfileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OptimizationProfile p ? LabelsPt.Of(p) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
