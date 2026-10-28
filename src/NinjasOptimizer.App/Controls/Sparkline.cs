using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace NinjasOptimizer.App.Controls;

/// <summary>Gráfico de linha minimalista (0–100) desenhado direto no OnRender: sem dependências e com custo mínimo.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x8B, 0x45, 0xE0)), FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private static readonly Brush Fill = CreateFill();

    private static Brush CreateFill()
    {
        var b = new LinearGradientBrush(Color.FromArgb(0x66, 0x6A, 0x1F, 0xC2), Color.FromArgb(0x00, 0x44, 0x08, 0x8C), 90);
        b.Freeze();
        return b;
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += s.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.ToList();
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var step = ActualWidth / (values.Count - 1);
        Point P(int i) => new(i * step, ActualHeight - Math.Clamp(values[i], 0, 100) / 100 * ActualHeight);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, ActualHeight), true, true);
            for (var i = 0; i < values.Count; i++) ctx.LineTo(P(i), true, true);
            ctx.LineTo(new Point(ActualWidth, ActualHeight), true, true);
        }
        area.Freeze();

        dc.DrawGeometry(Fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
