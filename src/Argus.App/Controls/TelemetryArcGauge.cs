using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argus.App.Controls;

/// <summary>
/// Hardware-accelerated Skia radial telemetry gauge rendered directly through Avalonia DrawingContext.
/// </summary>
public sealed class TelemetryArcGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, double>(nameof(Value), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<string> DisplayValueProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, string>(nameof(DisplayValue), "—");

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, string>(nameof(Unit), string.Empty);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> SubLabelProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, string>(nameof(SubLabel), string.Empty);

    public static readonly StyledProperty<Color> AccentStartProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, Color>(nameof(AccentStart), Color.Parse("#7C5CFF"));

    public static readonly StyledProperty<Color> AccentEndProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, Color>(nameof(AccentEnd), Color.Parse("#38D9F5"));

    public static readonly StyledProperty<bool> IsStandbyProperty =
        AvaloniaProperty.Register<TelemetryArcGauge, bool>(nameof(IsStandby), false);

    private static readonly Typeface ValueTypeface = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
    private static readonly SolidColorBrush PrimaryTextBrush = new(Color.Parse("#EDF1FF"));
    private static readonly SolidColorBrush SecondaryTextBrush = new(Color.Parse("#95A0CC"));
    private static readonly SolidColorBrush MutedTextBrush = new(Color.Parse("#5E6A96"));
    private static readonly Pen TrackPen = new(new SolidColorBrush(Color.Parse("#1C244D")), 9, lineCap: PenLineCap.Round);
    private static readonly Pen InnerRingPen = new(new SolidColorBrush(Color.Parse("#232C5E")), 1);
    private static readonly Pen TickPen = new(new SolidColorBrush(Color.Parse("#364385")), 1.2);

    // Instance-level geometry, brush, and text caches to eliminate per-frame Skia/HarfBuzz GC churn.
    private double _cachedWidth = -1;
    private double _cachedHeight = -1;
    private double _cachedRadius;
    private Point _cachedCenter;
    private StreamGeometry? _cachedTrackGeometry;
    private (Point P1, Point P2)[]? _cachedTicks;

    private Color _cachedAccentStart;
    private Color _cachedAccentEnd;
    private Pen? _cachedGlowPen;
    private Pen? _cachedArcPen;

    private string? _cachedValueTextKey;
    private double _cachedValueFontSize;
    private bool _cachedValueStandby;
    private FormattedText? _cachedValueText;

    private string? _cachedUnitKey;
    private FormattedText? _cachedUnitText;

    private string? _cachedLabelKey;
    private FormattedText? _cachedLabelText;

    private string? _cachedSubLabelKey;
    private FormattedText? _cachedSubLabelText;

    static TelemetryArcGauge()
    {
        AffectsRender<TelemetryArcGauge>(
            ValueProperty,
            MaximumProperty,
            DisplayValueProperty,
            UnitProperty,
            LabelProperty,
            SubLabelProperty,
            AccentStartProperty,
            AccentEndProperty,
            IsStandbyProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string DisplayValue
    {
        get => GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string SubLabel
    {
        get => GetValue(SubLabelProperty);
        set => SetValue(SubLabelProperty, value);
    }

    public Color AccentStart
    {
        get => GetValue(AccentStartProperty);
        set => SetValue(AccentStartProperty, value);
    }

    public Color AccentEnd
    {
        get => GetValue(AccentEndProperty);
        set => SetValue(AccentEndProperty, value);
    }

    public bool IsStandby
    {
        get => GetValue(IsStandbyProperty);
        set => SetValue(IsStandbyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width < 40 || height < 40)
        {
            return;
        }

        const double startDeg = 135.0;
        const double sweepTotalDeg = 270.0;

        if (_cachedTrackGeometry is null ||
            _cachedTicks is null ||
            Math.Abs(_cachedWidth - width) > 0.25 ||
            Math.Abs(_cachedHeight - height) > 0.25)
        {
            _cachedWidth = width;
            _cachedHeight = height;

            double diameter = Math.Min(width, height - 34);
            if (diameter < 40)
            {
                diameter = Math.Min(width, height);
            }

            _cachedRadius = (diameter / 2.0) - 12.0;
            _cachedCenter = new Point(width / 2.0, (diameter / 2.0) + 4.0);
            _cachedTrackGeometry = CreateArcGeometry(_cachedCenter, _cachedRadius, startDeg, sweepTotalDeg);

            const int tickCount = 19;
            var ticks = new (Point, Point)[tickCount];
            for (int i = 0; i < tickCount; i++)
            {
                double deg = startDeg + (sweepTotalDeg * i / (tickCount - 1));
                double rad = deg * Math.PI / 180.0;
                bool isMajor = i % 3 == 0;
                double inner = _cachedRadius - (isMajor ? 16 : 13.5);
                double outer = _cachedRadius - 9.5;
                var p1 = new Point(_cachedCenter.X + inner * Math.Cos(rad), _cachedCenter.Y + inner * Math.Sin(rad));
                var p2 = new Point(_cachedCenter.X + outer * Math.Cos(rad), _cachedCenter.Y + outer * Math.Sin(rad));
                ticks[i] = (p1, p2);
            }

            _cachedTicks = ticks;
        }

        double radius = _cachedRadius;
        Point center = _cachedCenter;

        // Subtle inner technical ring
        context.DrawEllipse(null, InnerRingPen, center, radius - 11, radius - 11);

        // Degree ticks along the arc (cached geometry)
        foreach (var (p1, p2) in _cachedTicks)
        {
            context.DrawLine(TickPen, p1, p2);
        }

        // Background track arc (cached geometry)
        context.DrawGeometry(null, TrackPen, _cachedTrackGeometry);

        // Active luminous value arc
        double ratio = Maximum > 0 && !double.IsNaN(Value)
            ? Math.Clamp(Value / Maximum, 0.0, 1.0)
            : 0.0;

        if (!IsStandby && ratio > 0.002)
        {
            double activeSweep = Math.Max(3.0, sweepTotalDeg * ratio);
            var activeGeometry = CreateArcGeometry(center, radius, startDeg, activeSweep);

            EnsureAccentPens();
            context.DrawGeometry(null, _cachedGlowPen!, activeGeometry);
            context.DrawGeometry(null, _cachedArcPen!, activeGeometry);
        }

        // Center numeric readout (cached FormattedText when unchanged)
        double valueFontSize = Math.Clamp(radius * 0.42, 16, 28);
        string displayKey = DisplayValue ?? "—";
        if (_cachedValueText is null ||
            !string.Equals(_cachedValueTextKey, displayKey, StringComparison.Ordinal) ||
            Math.Abs(_cachedValueFontSize - valueFontSize) > 0.1 ||
            _cachedValueStandby != IsStandby)
        {
            _cachedValueTextKey = displayKey;
            _cachedValueFontSize = valueFontSize;
            _cachedValueStandby = IsStandby;
            _cachedValueText = new FormattedText(
                displayKey,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ValueTypeface,
                valueFontSize,
                IsStandby ? MutedTextBrush : PrimaryTextBrush);
        }

        context.DrawText(_cachedValueText, new Point(center.X - _cachedValueText.Width / 2.0, center.Y - _cachedValueText.Height / 2.0 - 6));

        if (!string.IsNullOrWhiteSpace(Unit))
        {
            if (_cachedUnitText is null || !string.Equals(_cachedUnitKey, Unit, StringComparison.Ordinal))
            {
                _cachedUnitKey = Unit;
                _cachedUnitText = new FormattedText(
                    Unit,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    11.5,
                    SecondaryTextBrush);
            }

            context.DrawText(_cachedUnitText, new Point(center.X - _cachedUnitText.Width / 2.0, center.Y + _cachedValueText.Height / 2.0 - 6));
        }

        // Bottom caption below the gauge arc
        double captionTop = center.Y + radius * 0.72;
        if (!string.IsNullOrWhiteSpace(Label))
        {
            if (_cachedLabelText is null || !string.Equals(_cachedLabelKey, Label, StringComparison.Ordinal))
            {
                _cachedLabelKey = Label;
                _cachedLabelText = new FormattedText(
                    Label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    13,
                    PrimaryTextBrush);
            }

            context.DrawText(_cachedLabelText, new Point(center.X - _cachedLabelText.Width / 2.0, captionTop));
        }

        if (!string.IsNullOrWhiteSpace(SubLabel))
        {
            if (_cachedSubLabelText is null || !string.Equals(_cachedSubLabelKey, SubLabel, StringComparison.Ordinal))
            {
                _cachedSubLabelKey = SubLabel;
                _cachedSubLabelText = new FormattedText(
                    SubLabel,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    11,
                    MutedTextBrush);
            }

            context.DrawText(_cachedSubLabelText, new Point(center.X - _cachedSubLabelText.Width / 2.0, captionTop + 18));
        }
    }

    private void EnsureAccentPens()
    {
        if (_cachedGlowPen is not null &&
            _cachedArcPen is not null &&
            _cachedAccentStart == AccentStart &&
            _cachedAccentEnd == AccentEnd)
        {
            return;
        }

        _cachedAccentStart = AccentStart;
        _cachedAccentEnd = AccentEnd;

        var glowBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(65, AccentStart.R, AccentStart.G, AccentStart.B), 0),
                new GradientStop(Color.FromArgb(80, AccentEnd.R, AccentEnd.G, AccentEnd.B), 1)
            ]
        };
        _cachedGlowPen = new Pen(glowBrush, 16, lineCap: PenLineCap.Round);

        var arcBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(AccentStart, 0),
                new GradientStop(AccentEnd, 1)
            ]
        };
        _cachedArcPen = new Pen(arcBrush, 8.5, lineCap: PenLineCap.Round);
    }

    private static StreamGeometry CreateArcGeometry(Point center, double radius, double startDeg, double sweepDeg)
    {
        var geometry = new StreamGeometry();
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = (startDeg + sweepDeg) * Math.PI / 180.0;

        var startPoint = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var endPoint = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        using var ctx = geometry.Open();
        ctx.BeginFigure(startPoint, isFilled: false);
        ctx.ArcTo(
            endPoint,
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: sweepDeg > 180.0,
            sweepDirection: SweepDirection.Clockwise);
        ctx.EndFigure(isClosed: false);

        return geometry;
    }
}
