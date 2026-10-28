using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NinjaOptimizer.App.Controls;

/// <summary>
/// Gráfico "uso nos últimos 60 segundos" do sistema de design: grade em 0/33/66/100%,
/// série principal contínua em Roxo 300 com área em degradê e série secundária tracejada em azul.
/// Desenhado direto no OnRender: leve e sem dependências.
/// </summary>
public sealed class UsageChart : FrameworkElement
{
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(IEnumerable<double>), typeof(UsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(IEnumerable<double>), typeof(UsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public IEnumerable<double>? Primary { get => (IEnumerable<double>?)GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
    public IEnumerable<double>? Secondary { get => (IEnumerable<double>?)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    private const double LeftAxis = 44, BottomAxis = 26, Top = 8;

    private static readonly Pen GridPen = Frozen(new Pen(Brush("#2A2438"), 1));
    private static readonly Pen PrimaryPen = Frozen(new Pen(Brush("#B98BFF"), 2.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen SecondaryPen = Frozen(new Pen(Brush("#6FA8FF"), 2) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle([2.5, 2.5], 0), DashCap = PenLineCap.Round });
    private static readonly Brush AreaBrush = Frozen(new LinearGradientBrush(Color.FromArgb(0x73, 0x9A, 0x55, 0xF2), Color.FromArgb(0x00, 0x9A, 0x55, 0xF2), 90));
    private static readonly Brush LabelBrush = Brush("#8F88A3");
    private static readonly Typeface LabelFace = new(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static Brush Brush(string hex) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (UsageChart)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += chart.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= LeftAxis + 10 || h <= BottomAxis + Top + 10) return;

        var plotW = w - LeftAxis;
        var plotH = h - BottomAxis - Top;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Grade e rótulos do eixo Y
        foreach (var (pct, label) in new[] { (100, "100%"), (66, "66%"), (33, "33%"), (0, "0%") })
        {
            var y = Top + plotH * (1 - pct / 100.0);
            dc.DrawLine(GridPen, new Point(LeftAxis, y), new Point(w, y));
            dc.DrawText(Text(label, dpi), new Point(0, y - 8));
        }
        dc.DrawText(Text("-60 s", dpi), new Point(LeftAxis, h - 18));
        var now = Text("agora", dpi);
        dc.DrawText(now, new Point(w - now.Width, h - 18));

        Point P(IReadOnlyList<double> v, int i) => new(
            LeftAxis + plotW * i / Math.Max(1, v.Count - 1),
            Top + plotH * (1 - Math.Clamp(v[i], 0, 100) / 100));

        if (Primary?.ToList() is { Count: > 1 } p)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(LeftAxis, Top + plotH), true, true);
                for (var i = 0; i < p.Count; i++) ctx.LineTo(P(p, i), true, true);
                ctx.LineTo(new Point(w, Top + plotH), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(AreaBrush, null, area);
        }

        if (Secondary?.ToList() is { Count: > 1 } s) dc.DrawGeometry(null, SecondaryPen, Line(s, P));
        if (Primary?.ToList() is { Count: > 1 } p2) dc.DrawGeometry(null, PrimaryPen, Line(p2, P));
    }

    private static StreamGeometry Line(IReadOnlyList<double> v, Func<IReadOnlyList<double>, int, Point> at)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(at(v, 0), false, false);
            for (var i = 1; i < v.Count; i++) ctx.LineTo(at(v, i), true, true);
        }
        g.Freeze();
        return g;
    }

    private static FormattedText Text(string s, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12, LabelBrush, dpi);
}
