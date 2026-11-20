using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argus.App.Controls;

/// <summary>
/// Compact hardware-accelerated Skia mini-sparkline for dashboard overview cards.
/// </summary>
public sealed class TelemetrySparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<TelemetrySparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TelemetrySparkline, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<TelemetrySparkline, Color>(nameof(LineColor), Color.Parse("#38D9F5"));

    private static readonly Pen BaselinePen = new(new SolidColorBrush(Color.Parse("#1F2A56")), 1);

    private Color _cachedColor;
    private SolidColorBrush? _cachedDotBrush;
    private Pen? _cachedStrokePen;
    private LinearGradientBrush? _cachedFillBrush;

    static TelemetrySparkline()
    {
        AffectsRender<TelemetrySparkline>(ValuesProperty, MaximumProperty, LineColorProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 16 || h < 10)
        {
            return;
        }

        context.DrawLine(BaselinePen, new Point(0, h - 1), new Point(w, h - 1));

        var values = Values;
        if (values is null || values.Count == 0)
        {
            return;
        }

        double maxVal = Maximum > 0 && !double.IsNaN(Maximum) ? Maximum : 100.0;
        Color color = LineColor;
        if (_cachedStrokePen is null || _cachedFillBrush is null || _cachedDotBrush is null || _cachedColor != color)
        {
            _cachedColor = color;
            _cachedDotBrush = new SolidColorBrush(color);
            _cachedStrokePen = new Pen(_cachedDotBrush, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _cachedFillBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(64, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
                ]
            };
        }

        double MapY(double val)
        {
            double ratio = double.IsNaN(val) || double.IsInfinity(val) ? 0.0 : Math.Clamp(val / maxVal, 0.0, 1.0);
            return (h - 2.0) - ((h - 4.0) * ratio);
        }

        if (values.Count == 1)
        {
            double y = MapY(values[0]);
            var p1 = new Point(Math.Max(2, w - 16), y);
            var p2 = new Point(w - 2, y);
            context.DrawLine(_cachedStrokePen, p1, p2);
            context.DrawEllipse(_cachedDotBrush, null, p2, 2.3, 2.3);
            return;
        }

        int count = values.Count;
        double xStep = (w - 3.0) / (count - 1);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(1.0, h - 1), isFilled: true);
            for (int i = 0; i < count; i++)
            {
                ctx.LineTo(new Point((xStep * i) + 1.0, MapY(values[i])));
            }

            ctx.LineTo(new Point(w - 2.0, h - 1));
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(_cachedFillBrush, null, area);

        Point lastPoint = default;
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(1.0, MapY(values[0])), isFilled: false);
            for (int i = 1; i < count; i++)
            {
                lastPoint = new Point((xStep * i) + 1.0, MapY(values[i]));
                ctx.LineTo(lastPoint);
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, _cachedStrokePen, line);
        context.DrawEllipse(_cachedDotBrush, null, lastPoint, 2.3, 2.3);
    }
}
