using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.App.Controls;

public sealed class ProfileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OptimizationProfile p ? LabelsPt.Of(p) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Converte a chave de um recurso (ex.: "Icon.Cpu") no próprio recurso (a geometria do ícone).</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
