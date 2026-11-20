using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argus.App.Controls;

/// <summary>
/// Hardware-accelerated Skia multi-series real-time telemetry line chart rendered directly via DrawingContext.
/// </summary>
public sealed class TelemetryLineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PrimarySeriesProperty =
        AvaloniaProperty.Register<TelemetryLineChart, IReadOnlyList<double>?>(nameof(PrimarySeries));

    public static readonly StyledProperty<IReadOnlyList<double>?> SecondarySeriesProperty =
        AvaloniaProperty.Register<TelemetryLineChart, IReadOnlyList<double>?>(nameof(SecondarySeries));

    public static readonly StyledProperty<IReadOnlyList<double>?> TertiarySeriesProperty =
        AvaloniaProperty.Register<TelemetryLineChart, IReadOnlyList<double>?>(nameof(TertiarySeries));

    public static readonly StyledProperty<IReadOnlyList<double>?> ThermalSeriesProperty =
        AvaloniaProperty.Register<TelemetryLineChart, IReadOnlyList<double>?>(nameof(ThermalSeries));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TelemetryLineChart, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<int> WindowSecondsProperty =
        AvaloniaProperty.Register<TelemetryLineChart, int>(nameof(WindowSeconds), 60);

    public static readonly StyledProperty<double?> AlertThresholdProperty =
        AvaloniaProperty.Register<TelemetryLineChart, double?>(nameof(AlertThreshold), null);

    private static readonly Color CpuColor = Color.Parse("#38D9F5");
    private static readonly Color RamColor = Color.Parse("#9B82FF");
    private static readonly Color GpuColor = Color.Parse("#2DD4A8");
    private static readonly Color ThermalColor = Color.Parse("#F5A623");

    private static readonly SolidColorBrush PlotBackgroundBrush = new(Color.Parse("#080D20"));
    private static readonly Pen PlotBorderPen = new(new SolidColorBrush(Color.Parse("#242E63")), 1);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#162048")), 1);
    private static readonly Pen ThresholdPen = new(new SolidColorBrush(Color.Parse("#88F45B69")), 1.2, DashStyle.Dash);

    private static readonly SolidColorBrush CpuCoreBrush = new(CpuColor);
    private static readonly SolidColorBrush CpuHaloBrush = new(Color.FromArgb(80, CpuColor.R, CpuColor.G, CpuColor.B));
    private static readonly SolidColorBrush RamCoreBrush = new(RamColor);
    private static readonly SolidColorBrush RamHaloBrush = new(Color.FromArgb(80, RamColor.R, RamColor.G, RamColor.B));
    private static readonly SolidColorBrush GpuCoreBrush = new(GpuColor);
    private static readonly SolidColorBrush GpuHaloBrush = new(Color.FromArgb(80, GpuColor.R, GpuColor.G, GpuColor.B));
    private static readonly SolidColorBrush ThermalCoreBrush = new(ThermalColor);
    private static readonly SolidColorBrush ThermalHaloBrush = new(Color.FromArgb(80, ThermalColor.R, ThermalColor.G, ThermalColor.B));

    private static readonly Pen CpuPen = new(CpuCoreBrush, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly Pen RamPen = new(RamCoreBrush, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly Pen GpuPen = new(GpuCoreBrush, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly Pen ThermalPen = new(ThermalCoreBrush, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static readonly double[] GridLevels = [0.25, 0.50, 0.75];

    private static readonly LinearGradientBrush CpuAreaBrush = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.Parse("#3038D9F5"), 0),
            new GradientStop(Color.Parse("#0438D9F5"), 1)
        ]
    };

    private static readonly Typeface AxisTypeface = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyle.Normal, FontWeight.Normal);
    private static readonly SolidColorBrush AxisTextBrush = new(Color.Parse("#6874A3"));
    private static readonly SolidColorBrush DotGlowBrush = new(Color.Parse("#EDF1FF"));

    private static readonly FormattedText Label100 = CreateAxisText("100%");
    private static readonly FormattedText Label50 = CreateAxisText("50%");
    private static readonly FormattedText Label0 = CreateAxisText("0%");
    private static readonly FormattedText LabelNow = CreateAxisText("зараз");

    private int _cachedWindowSeconds = -1;
    private FormattedText? _cachedWindowLabel;

    static TelemetryLineChart()
    {
        AffectsRender<TelemetryLineChart>(
            PrimarySeriesProperty,
            SecondarySeriesProperty,
            TertiarySeriesProperty,
            ThermalSeriesProperty,
            MaximumProperty,
            WindowSecondsProperty,
            AlertThresholdProperty);
    }

    public IReadOnlyList<double>? PrimarySeries
    {
        get => GetValue(PrimarySeriesProperty);
        set => SetValue(PrimarySeriesProperty, value);
    }

    public IReadOnlyList<double>? SecondarySeries
    {
        get => GetValue(SecondarySeriesProperty);
        set => SetValue(SecondarySeriesProperty, value);
    }

    public IReadOnlyList<double>? TertiarySeries
    {
        get => GetValue(TertiarySeriesProperty);
        set => SetValue(TertiarySeriesProperty, value);
    }

    public IReadOnlyList<double>? ThermalSeries
    {
        get => GetValue(ThermalSeriesProperty);
        set => SetValue(ThermalSeriesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public double? AlertThreshold
    {
        get => GetValue(AlertThresholdProperty);
        set => SetValue(AlertThresholdProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double totalW = Bounds.Width;
        double totalH = Bounds.Height;
        if (totalW < 80 || totalH < 60)
        {
            return;
        }

        const double rightAxisWidth = 42.0;
        const double bottomAxisHeight = 18.0;
        const double topPad = 4.0;
        const double leftPad = 2.0;

        var plotRect = new Rect(
            leftPad,
            topPad,
            Math.Max(20.0, totalW - leftPad - rightAxisWidth),
            Math.Max(20.0, totalH - topPad - bottomAxisHeight));

        // Rounded chart surface
        context.DrawRectangle(PlotBackgroundBrush, PlotBorderPen, plotRect, 8.0, 8.0);

        // Horizontal gridlines (25%, 50%, 75%)
        foreach (double ratio in GridLevels)
        {
            double y = plotRect.Bottom - (plotRect.Height * ratio);
            context.DrawLine(GridPen, new Point(plotRect.Left + 1, y), new Point(plotRect.Right - 1, y));
        }

        // Vertical gridlines (4 divisions)
        for (int div = 1; div < 4; div++)
        {
            double x = plotRect.Left + (plotRect.Width * div / 4.0);
            context.DrawLine(GridPen, new Point(x, plotRect.Top + 1), new Point(x, plotRect.Bottom - 1));
        }

        double maxVal = Maximum > 0 && !double.IsNaN(Maximum) ? Maximum : 100.0;

        // Right Y-axis mono labels (cached static FormattedText)
        context.DrawText(Label100, new Point(plotRect.Right + 6, plotRect.Top + 1));
        context.DrawText(Label50, new Point(plotRect.Right + 6, plotRect.Top + (plotRect.Height * 0.5) - 6));
        context.DrawText(Label0, new Point(plotRect.Right + 6, plotRect.Bottom - 12));

        // Bottom X-axis time labels
        int winSec = Math.Max(10, WindowSeconds);
        if (_cachedWindowLabel is null || _cachedWindowSeconds != winSec)
        {
            _cachedWindowSeconds = winSec;
            string leftLabel = winSec >= 60 && winSec % 60 == 0
                ? $"-{winSec / 60} хв"
                : $"-{winSec} с";
            _cachedWindowLabel = CreateAxisText(leftLabel);
        }

        context.DrawText(_cachedWindowLabel, new Point(plotRect.Left + 4, plotRect.Bottom + 3));
        context.DrawText(LabelNow, new Point(plotRect.Right - 36, plotRect.Bottom + 3));

        var innerClip = plotRect.Deflate(new Thickness(2));
        using (context.PushClip(innerClip))
        {
            // Alert threshold dashed line
            if (AlertThreshold is { } threshold and > 0 and <= 115)
            {
                double threshRatio = Math.Clamp(threshold / maxVal, 0.0, 1.0);
                double threshY = plotRect.Bottom - (plotRect.Height * threshRatio);
                context.DrawLine(ThresholdPen, new Point(plotRect.Left + 2, threshY), new Point(plotRect.Right - 2, threshY));
            }

            // Plot series back-to-front so CPU cyan stands out on top
            DrawSeries(context, innerClip, SecondarySeries, maxVal, RamPen, RamCoreBrush, RamHaloBrush, fillArea: false);
            DrawSeries(context, innerClip, TertiarySeries, maxVal, GpuPen, GpuCoreBrush, GpuHaloBrush, fillArea: false);
            DrawSeries(context, innerClip, ThermalSeries, maxVal, ThermalPen, ThermalCoreBrush, ThermalHaloBrush, fillArea: false);
            DrawSeries(context, innerClip, PrimarySeries, maxVal, CpuPen, CpuCoreBrush, CpuHaloBrush, fillArea: true);
        }
    }

    private static void DrawSeries(
        DrawingContext context,
        Rect plot,
        IReadOnlyList<double>? series,
        double maximum,
        Pen strokePen,
        IBrush coreBrush,
        IBrush haloBrush,
        bool fillArea)
    {
        if (series is null || series.Count == 0)
        {
            return;
        }

        double usableHeight = Math.Max(4.0, plot.Height - 6.0);
        double MapY(double val)
        {
            double clamped = double.IsNaN(val) || double.IsInfinity(val) ? 0.0 : Math.Clamp(val / maximum, 0.0, 1.0);
            return (plot.Bottom - 3.0) - (usableHeight * clamped);
        }

        if (series.Count == 1)
        {
            double y = MapY(series[0]);
            var pStart = new Point(Math.Max(plot.Left + 2, plot.Right - 36), y);
            var pEnd = new Point(plot.Right - 3, y);
            context.DrawLine(strokePen, pStart, pEnd);
            DrawEndpoint(context, pEnd, coreBrush, haloBrush);
            return;
        }

        int count = series.Count;
        double xStep = plot.Width / (count - 1);

        if (fillArea)
        {
            var areaGeometry = new StreamGeometry();
            using (var ctx = areaGeometry.Open())
            {
                ctx.BeginFigure(new Point(plot.Left, plot.Bottom), isFilled: true);
                for (int i = 0; i < count; i++)
                {
                    ctx.LineTo(new Point(plot.Left + (xStep * i), MapY(series[i])));
                }

                ctx.LineTo(new Point(plot.Left + plot.Width, plot.Bottom));
                ctx.EndFigure(isClosed: true);
            }

            context.DrawGeometry(CpuAreaBrush, null, areaGeometry);
        }

        Point lastPoint = default;
        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            var first = new Point(plot.Left, MapY(series[0]));
            ctx.BeginFigure(first, isFilled: false);
            for (int i = 1; i < count; i++)
            {
                lastPoint = new Point(plot.Left + (xStep * i), MapY(series[i]));
                ctx.LineTo(lastPoint);
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, strokePen, lineGeometry);
        DrawEndpoint(context, lastPoint, coreBrush, haloBrush);
    }

    private static void DrawEndpoint(DrawingContext context, Point center, IBrush coreBrush, IBrush haloBrush)
    {
        context.DrawEllipse(haloBrush, null, center, 5.5, 5.5);
        context.DrawEllipse(coreBrush, null, center, 3.0, 3.0);
        context.DrawEllipse(DotGlowBrush, null, center, 1.2, 1.2);
    }

    private static FormattedText CreateAxisText(string text)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            10.5,
            AxisTextBrush);
    }
}
