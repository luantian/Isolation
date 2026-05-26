using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class GaugeCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(GaugeCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeCard), new PropertyMetadata(0d, OnGaugeValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(GaugeCard), new PropertyMetadata(0d, OnGaugeValueChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(GaugeCard), new PropertyMetadata(100d, OnGaugeValueChanged));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(GaugeCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(GaugeCard), new PropertyMetadata(Brushes.DodgerBlue));

    public static readonly DependencyProperty NeedleAngleProperty =
        DependencyProperty.Register(nameof(NeedleAngle), typeof(double), typeof(GaugeCard), new PropertyMetadata(-90d));

    public static readonly DependencyProperty ValueArcDataProperty =
        DependencyProperty.Register(nameof(ValueArcData), typeof(Geometry), typeof(GaugeCard), new PropertyMetadata(Geometry.Empty));

    public static readonly DependencyProperty DisplayValueProperty =
        DependencyProperty.Register(nameof(DisplayValue), typeof(string), typeof(GaugeCard), new PropertyMetadata("0.0"));

    public GaugeCard()
    {
        InitializeComponent();
        RefreshGauge();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double NeedleAngle
    {
        get => (double)GetValue(NeedleAngleProperty);
        private set => SetValue(NeedleAngleProperty, value);
    }

    public Geometry ValueArcData
    {
        get => (Geometry)GetValue(ValueArcDataProperty);
        private set => SetValue(ValueArcDataProperty, value);
    }

    public string DisplayValue
    {
        get => (string)GetValue(DisplayValueProperty);
        private set => SetValue(DisplayValueProperty, value);
    }

    private static void OnGaugeValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((GaugeCard)d).RefreshGauge();
    }

    private void RefreshGauge()
    {
        var span = Math.Max(1, Maximum - Minimum);
        var ratio = Math.Clamp((Value - Minimum) / span, 0, 1);
        NeedleAngle = -65 + ratio * 130;
        ValueArcData = CreateArcGeometry(ratio);
        DisplayValue = Value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static Geometry CreateArcGeometry(double ratio)
    {
        if (ratio <= 0)
        {
            return Geometry.Empty;
        }

        const double centerX = 110;
        const double centerY = 112;
        const double radius = 82;
        var startAngle = 180d;
        var endAngle = 180d - 180d * ratio;

        var start = PointOnCircle(centerX, centerY, radius, startAngle);
        var end = PointOnCircle(centerX, centerY, radius, endAngle);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });

        return new PathGeometry(new[] { figure });
    }

    private static Point PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
    {
        var angle = Math.PI * angleDegrees / 180d;
        return new Point(centerX + radius * Math.Cos(angle), centerY - radius * Math.Sin(angle));
    }
}
