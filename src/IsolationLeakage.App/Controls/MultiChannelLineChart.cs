using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public class MultiChannelLineChart : FrameworkElement
{
    // 通道颜色定义
    private static readonly Color PressureColor = Color.FromRgb(0x07, 0x58, 0xD8);
    private static readonly Color FlowColor = Color.FromRgb(0x12, 0xA3, 0x66);
    private static readonly Color TempColor = Color.FromRgb(0xF9, 0x73, 0x16);

    private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromRgb(0xDE, 0xE4, 0xEE)), 1);
    private readonly Pen _axisPen = new(new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC)), 1.5);

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    // -- pressure --
    public static readonly DependencyProperty PressurePointsProperty =
        DependencyProperty.Register(nameof(PressurePoints), typeof(ObservableCollection<double>), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? PressurePoints
    {
        get => (ObservableCollection<double>?)GetValue(PressurePointsProperty);
        set => SetValue(PressurePointsProperty, value);
    }

    public static readonly DependencyProperty PressureMinProperty =
        DependencyProperty.Register(nameof(PressureMin), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PressureMin
    {
        get => (double)GetValue(PressureMinProperty);
        set => SetValue(PressureMinProperty, value);
    }

    public static readonly DependencyProperty PressureMaxProperty =
        DependencyProperty.Register(nameof(PressureMax), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PressureMax
    {
        get => (double)GetValue(PressureMaxProperty);
        set => SetValue(PressureMaxProperty, value);
    }

    // -- flow --
    public static readonly DependencyProperty FlowPointsProperty =
        DependencyProperty.Register(nameof(FlowPoints), typeof(ObservableCollection<double>), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? FlowPoints
    {
        get => (ObservableCollection<double>?)GetValue(FlowPointsProperty);
        set => SetValue(FlowPointsProperty, value);
    }

    public static readonly DependencyProperty FlowMinProperty =
        DependencyProperty.Register(nameof(FlowMin), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double FlowMin
    {
        get => (double)GetValue(FlowMinProperty);
        set => SetValue(FlowMinProperty, value);
    }

    public static readonly DependencyProperty FlowMaxProperty =
        DependencyProperty.Register(nameof(FlowMax), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double FlowMax
    {
        get => (double)GetValue(FlowMaxProperty);
        set => SetValue(FlowMaxProperty, value);
    }

    // -- temp --
    public static readonly DependencyProperty TempPointsProperty =
        DependencyProperty.Register(nameof(TempPoints), typeof(ObservableCollection<double>), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? TempPoints
    {
        get => (ObservableCollection<double>?)GetValue(TempPointsProperty);
        set => SetValue(TempPointsProperty, value);
    }

    public static readonly DependencyProperty TempMinProperty =
        DependencyProperty.Register(nameof(TempMin), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TempMin
    {
        get => (double)GetValue(TempMinProperty);
        set => SetValue(TempMinProperty, value);
    }

    public static readonly DependencyProperty TempMaxProperty =
        DependencyProperty.Register(nameof(TempMax), typeof(double), typeof(MultiChannelLineChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TempMax
    {
        get => (double)GetValue(TempMaxProperty);
        set => SetValue(TempMaxProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, availableSize.Height);
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w <= 1 || h <= 1) return;

        const double pad = 4;
        double cw = w - pad * 2;
        double ch = h - pad * 2;

        // 背景
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        // 内边框
        dc.DrawRectangle(null, _axisPen, new Rect(pad, pad, cw, ch));

        // 网格线 + 标签
        int gridLines = 5;
        for (int i = 0; i <= gridLines; i++)
        {
            double ratio = (double)i / gridLines;
            double y = pad + ch * ratio;
            dc.DrawLine(_gridPen, new Point(pad, y), new Point(w - pad, y));
        }

        // 绘制三条通道（填充 + 折线 + 末端点）
        DrawChannel(PressurePoints, PressureMin, PressureMax, PressureColor, dc, pad, cw, ch);
        DrawChannel(FlowPoints, FlowMin, FlowMax, FlowColor, dc, pad, cw, ch);
        DrawChannel(TempPoints, TempMin, TempMax, TempColor, dc, pad, cw, ch);
    }

    private void DrawChannel(
        ObservableCollection<double>? data, double min, double max, Color color,
        DrawingContext dc, double pad, double cw, double ch)
    {
        if (data == null || data.Count < 2) return;

        double range = max - min;
        if (range == 0) range = 1;

        // 构建路径点
        var pts = new Point[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            double x = pad + i * cw / (data.Count - 1);
            double ratio = (data[i] - min) / range;
            // 将值限制在 [0, 1] 范围内，超出则贴边
            ratio = Math.Max(0, Math.Min(1, ratio));
            double y = pad + ch * (1 - ratio);
            pts[i] = new Point(x, y);
        }

        // 填充区域 (折线到底部的半透明渐变)
        var fillGeometry = new StreamGeometry();
        using (var ctx = fillGeometry.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++)
                ctx.LineTo(pts[i], true, false);
            // 闭合到底部
            ctx.LineTo(new Point(pts[pts.Length - 1].X, pad + ch), true, false);
            ctx.LineTo(new Point(pts[0].X, pad + ch), true, false);
        }
        fillGeometry.Freeze();

        var fillBrush = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B));
        dc.DrawGeometry(fillBrush, null, fillGeometry);

        // 折线
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++)
                ctx.LineTo(pts[i], true, false);
        }
        lineGeo.Freeze();

        var linePen = new Pen(new SolidColorBrush(color), 1.2);
        dc.DrawGeometry(null, linePen, lineGeo);

        // 末端圆点 (带白色边框)
        var lastPt = pts[pts.Length - 1];
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(color), 2), lastPt, 5, 5);
        dc.DrawEllipse(new SolidColorBrush(color), null, lastPt, 2.5, 2.5);
    }
}
