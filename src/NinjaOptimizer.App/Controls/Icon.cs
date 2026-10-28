using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NinjaOptimizer.App.Controls;

/// <summary>
/// Ícone de contorno do sistema de design (grade 24, traço 1,75, pontas arredondadas).
/// A cor vem de <see cref="Control.Foreground"/>; o tamanho, de <see cref="Size"/>.
/// </summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(20d));

    static Icon()
    {
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}
