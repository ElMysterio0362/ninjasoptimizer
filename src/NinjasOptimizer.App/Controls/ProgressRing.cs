using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NinjasOptimizer.App.Controls;

/// <summary>Anel de progresso (0–100) com gradiente de destaque e animação suave ao mudar de valor.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new PropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        "AnimatedValue", typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static readonly Brush Stroke = CreateStroke();

    private static Brush CreateStroke()
    {
        var b = new LinearGradientBrush(Color.FromRgb(0x7B, 0x2F, 0xF7), Color.FromRgb(0x44, 0x08, 0x8C), 45);
        b.Freeze();
        return b;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ProgressRing)d;
        ring.BeginAnimation(AnimatedValueProperty, new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var value = Math.Clamp((double)GetValue(AnimatedValueProperty), 0, 100);
        if (value <= 0) return;

        var r = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (value >= 99.99)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }

        var angle = value / 100 * 360;
        Point At(double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            ctx.ArcTo(At(angle), new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
