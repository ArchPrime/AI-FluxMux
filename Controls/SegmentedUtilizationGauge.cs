using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FluxMux.Avalonia.Controls;

public sealed class SegmentedUtilizationGauge : Control
{
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<SegmentedUtilizationGauge, double>(nameof(Percent), 0d);

    static SegmentedUtilizationGauge()
    {
        AffectsRender<SegmentedUtilizationGauge>(PercentProperty);
        AffectsMeasure<SegmentedUtilizationGauge>(PercentProperty);
    }

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var em = ResolveEm();
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : em * 14;
        return new Size(width, em * 1.2);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var em = ResolveEm();
        var gap = Math.Max(1, em * 0.12);
        var minSegment = Math.Max(2.5, em * 0.28);
        var count = (int)Math.Floor((bounds.Width + gap) / (minSegment + gap));
        count = Math.Clamp(count, 10, 40);
        var segmentWidth = (bounds.Width - gap * (count - 1)) / count;
        if (segmentWidth <= 0)
        {
            return;
        }

        var litThrough = Math.Clamp(Percent, 0, 100) / 100.0 * count;
        var radius = Math.Min(em * 0.18, segmentWidth / 2);

        for (var i = 0; i < count; i++)
        {
            var x = i * (segmentWidth + gap);
            var rect = new Rect(x, 0, segmentWidth, bounds.Height);
            var t = count == 1 ? 0 : (double)i / (count - 1);
            var color = ColorAt(t);
            var lit = i + 1 <= litThrough || (i < litThrough && i + 1 > litThrough);
            if (!lit && i >= (int)Math.Ceiling(litThrough))
            {
                color = Color.FromArgb(48, color.R, color.G, color.B);
            }
            else if (!lit)
            {
                color = Color.FromArgb(90, color.R, color.G, color.B);
            }

            context.DrawRectangle(new SolidColorBrush(color), null, rect, radius, radius);
        }
    }

    public static Color ColorAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t <= 0.5)
        {
            return Lerp(Color.FromRgb(34, 197, 94), Color.FromRgb(250, 204, 21), t / 0.5);
        }

        return Lerp(Color.FromRgb(250, 204, 21), Color.FromRgb(239, 68, 68), (t - 0.5) / 0.5);
    }

    private double ResolveEm()
    {
        var fromAncestor = GetValue(TextBlock.FontSizeProperty);
        return fromAncestor > 0 && double.IsFinite(fromAncestor) ? fromAncestor : 11;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }
}
