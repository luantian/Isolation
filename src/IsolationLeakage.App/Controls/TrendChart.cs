using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 工业风格趋势图组件（基于 OxyPlot 封装）
/// 自定义 Tracker 浮层：固定尺寸、鼠标悬停显示所有通道值。
/// </summary>
public class TrendChart : ContentControl
{
    private static readonly OxyColor ColorPressure = OxyColor.FromRgb(0x07, 0x58, 0xD8);
    private static readonly OxyColor ColorFlow = OxyColor.FromRgb(0x12, 0xA3, 0x66);
    private static readonly OxyColor ColorTemp = OxyColor.FromRgb(0xF9, 0x73, 0x16);
    private static readonly OxyColor ColorPrimary = OxyColor.FromRgb(0x07, 0x58, 0xD8);
    private static readonly OxyColor ColorGrid = OxyColor.FromArgb(0x40, 0xDE, 0xE4, 0xEE);

    private static readonly SolidColorBrush WpfPressure = new(Color.FromRgb(0x07, 0x58, 0xD8));
    private static readonly SolidColorBrush WpfFlow = new(Color.FromRgb(0x12, 0xA3, 0x66));
    private static readonly SolidColorBrush WpfTemp = new(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly SolidColorBrush WpfPrimary = new(Color.FromRgb(0x07, 0x58, 0xD8));

    // Tracker 固定尺寸
    private const double TrackerWidth = 180;
    private const double TrackerHeight = 120;

    private readonly OxyPlot.Wpf.PlotView _plotView;
    private readonly PlotModel _model;
    private readonly LineSeries _pressureSeries;
    private readonly LineSeries _flowSeries;
    private readonly LineSeries _tempSeries;
    private readonly LineSeries _primarySeries;
    private readonly LinearAxis _xAxis;
    private readonly LinearAxis _yAxis;
    private readonly LinearAxis _yAxisSecondary;
    private DisplayMode _mode;

    private bool _isPanning;

    // Tracker 浮层
    private readonly Border _trackerBorder;
    private readonly TextBlock _tbHeader;
    private readonly StackPanel _sp1;
    private readonly StackPanel _sp2;
    private readonly StackPanel _sp3;
    private readonly Line _trackerLine;
    private readonly Grid _overlayGrid;

    public TrendChart()
    {
        _model = new PlotModel
        {
            PlotAreaBackground = OxyColors.Transparent,
            PlotAreaBorderColor = OxyColors.Transparent,
            Padding = new OxyThickness(4, 4, 12, 4),
        };

        _xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom, Title = "采样点",
            TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            AxislineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = ColorGrid,
            MajorGridlineThickness = 1, MinorGridlineStyle = LineStyle.None,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _model.Axes.Add(_xAxis);

        _yAxis = new LinearAxis
        {
            Position = AxisPosition.Left, Title = "值",
            TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            AxislineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = ColorGrid,
            MajorGridlineThickness = 1, MinorGridlineStyle = LineStyle.None,
            IsZoomEnabled = true, IsPanEnabled = true,
            MinimumPadding = 0.1, MaximumPadding = 0.1,
        };
        _model.Axes.Add(_yAxis);

        _yAxisSecondary = new LinearAxis
        {
            Position = AxisPosition.Right, Key = "Secondary",
            IsAxisVisible = false, IsZoomEnabled = false, IsPanEnabled = false,
        };
        _model.Axes.Add(_yAxisSecondary);

        _pressureSeries = CreateSeries("压力 (MPa)", ColorPressure);
        _model.Series.Add(_pressureSeries);
        _flowSeries = CreateSeries("泄漏率 (L/min)", ColorFlow);
        _model.Series.Add(_flowSeries);
        _tempSeries = CreateSeries("温度 (℃)", ColorTemp);
        _model.Series.Add(_tempSeries);
        _primarySeries = CreateSeries("", ColorPrimary);
        _primarySeries.IsVisible = false;
        _model.Series.Add(_primarySeries);

        // Tracker 浮层 — 固定宽高
        _trackerBorder = new Border
        {
            Width = TrackerWidth,
            Height = TrackerHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var sp = new StackPanel();
        _tbHeader = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            FontSize = 13,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        sp.Children.Add(_tbHeader);

        _sp1 = CreateValueRow(WpfPressure, out var val1);
        _sp2 = CreateValueRow(WpfFlow, out var val2);
        _sp3 = CreateValueRow(WpfTemp, out var val3);
        sp.Children.Add(_sp1);
        sp.Children.Add(_sp2);
        sp.Children.Add(_sp3);

        _trackerBorder.Child = sp;

        // 垂直追踪线
        _trackerLine = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x94, 0xA3, 0xB8)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
        };

        // PlotView
        _plotView = new OxyPlot.Wpf.PlotView { Model = _model };

        // 叠加层
        _overlayGrid = new Grid { Background = Brushes.Transparent };
        _overlayGrid.Children.Add(_plotView);
        _overlayGrid.Children.Add(_trackerLine);
        _overlayGrid.Children.Add(_trackerBorder);

        Content = _overlayGrid;

        // 事件
        _plotView.MouseDown += OnMouseDown;
        _plotView.MouseUp += OnMouseUp;
        _plotView.MouseWheel += OnMouseWheel;
        _overlayGrid.MouseMove += OnMouseMove;
        _overlayGrid.MouseLeave += OnMouseLeave;

        _pressureHandler = (_, _) => { ResetZoom(); _plotView.InvalidatePlot(true); };
        _flowHandler = (_, _) => { ResetZoom(); _plotView.InvalidatePlot(true); };
        _tempHandler = (_, _) => { ResetZoom(); _plotView.InvalidatePlot(true); };
        _primaryHandler = (_, _) => { ResetZoom(); _plotView.InvalidatePlot(true); };
    }

    private readonly NotifyCollectionChangedEventHandler _pressureHandler;
    private readonly NotifyCollectionChangedEventHandler _flowHandler;
    private readonly NotifyCollectionChangedEventHandler _tempHandler;
    private readonly NotifyCollectionChangedEventHandler _primaryHandler;

    private static LineSeries CreateSeries(string title, OxyColor color)
    {
        return new LineSeries { Title = title, Color = color, StrokeThickness = 1.5, MarkerType = MarkerType.None };
    }

    private static StackPanel CreateValueRow(SolidColorBrush colorBrush, out TextBlock valBlock)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        sp.Children.Add(new TextBlock { Text = "●", Foreground = colorBrush, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        var lbl = new TextBlock { Width = 55, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), FontSize = 13, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        sp.Children.Add(lbl);
        valBlock = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = System.Windows.FontWeights.Medium, FontFamily = new FontFamily("Consolas"), VerticalAlignment = System.Windows.VerticalAlignment.Center };
        sp.Children.Add(valBlock);
        return sp;
    }

    public void ResetZoom() { _xAxis.Reset(); AutoScaleYAxis(); }

    #region Tracker

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_isPanning) return;

        var pos = e.GetPosition(_plotView);
        var sp = new ScreenPoint(pos.X, pos.Y);
        var pa = _model.PlotArea;

        if (pa.Width <= 0 || !pa.Contains(sp.X, sp.Y))
        {
            _trackerBorder.Visibility = Visibility.Collapsed;
            _trackerLine.Visibility = Visibility.Collapsed;
            return;
        }

        double dataX = _xAxis.InverseTransform(sp.X);
        int idx = (int)Math.Round(dataX);
        if (idx < 0)
        {
            _trackerBorder.Visibility = Visibility.Collapsed;
            _trackerLine.Visibility = Visibility.Collapsed;
            return;
        }

        _tbHeader.Text = $"采样点: {idx}";
        SetRowVisible(_sp1, "压力", WpfPressure, _pressureSeries, idx);
        SetRowVisible(_sp2, "泄漏率", WpfFlow, _flowSeries, idx);
        SetRowVisible(_sp3, "温度", WpfTemp, _tempSeries, idx);

        _trackerBorder.Visibility = Visibility.Visible;

        // 定位（相对 plotView 坐标）
        double x = pos.X + 15;
        double y = pos.Y - TrackerHeight / 2;
        if (x + TrackerWidth > _plotView.ActualWidth - 5) x = pos.X - TrackerWidth - 15;
        if (y < 5) y = 5;
        if (y + TrackerHeight > _plotView.ActualHeight - 5) y = _plotView.ActualHeight - TrackerHeight - 5;
        _trackerBorder.Margin = new Thickness(x, y, 0, 0);

        _trackerLine.Visibility = Visibility.Visible;
        _trackerLine.X1 = pos.X; _trackerLine.X2 = pos.X;
        _trackerLine.Y1 = pa.Top; _trackerLine.Y2 = pa.Bottom;
    }

    private static void SetRowVisible(StackPanel sp, string label, SolidColorBrush color, LineSeries series, int idx)
    {
        if (series.Points.Count > idx && series.IsVisible)
        {
            double v = series.Points[idx].Y;
            if (!double.IsNaN(v) && !double.IsInfinity(v))
            {
                var lbl = (TextBlock)sp.Children[1];
                var val = (TextBlock)sp.Children[2];
                lbl.Text = label;
                val.Text = $"{v:0.###}";
                sp.Visibility = Visibility.Visible;
                return;
            }
        }
        sp.Visibility = Visibility.Collapsed;
    }

    private void OnMouseLeave(object? sender, MouseEventArgs e)
    {
        _trackerBorder.Visibility = Visibility.Collapsed;
        _trackerLine.Visibility = Visibility.Collapsed;
    }

    #endregion

    public DisplayMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _pressureSeries.IsVisible = value == DisplayMode.ThreeChannel;
            _flowSeries.IsVisible = value == DisplayMode.ThreeChannel;
            _tempSeries.IsVisible = value == DisplayMode.ThreeChannel;
            _primarySeries.IsVisible = value == DisplayMode.SingleChannel;
            _plotView.InvalidatePlot(true);
        }
    }

    public string YAxisTitle
    {
        get => (string)GetValue(YAxisTitleProperty);
        set => SetValue(YAxisTitleProperty, value);
    }
    public static readonly DependencyProperty YAxisTitleProperty =
        DependencyProperty.Register(nameof(YAxisTitle), typeof(string), typeof(TrendChart),
            new FrameworkPropertyMetadata("值", OnYAxisTitleChanged));

    private static void OnYAxisTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TrendChart)d)._yAxis.Title = (string)e.NewValue;
        ((TrendChart)d)._plotView.InvalidatePlot(true);
    }

    public ObservableCollection<double>? PressurePoints
    {
        get => (ObservableCollection<double>?)GetValue(PressurePointsProperty);
        set => SetValue(PressurePointsProperty, value);
    }
    public static readonly DependencyProperty PressurePointsProperty =
        DependencyProperty.Register(nameof(PressurePoints), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnPressurePointsChanged));

    private static void OnPressurePointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._pressureHandler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._pressureHandler;
        c.SyncSeries(c._pressureSeries, np);
    }

    public ObservableCollection<double>? FlowPoints
    {
        get => (ObservableCollection<double>?)GetValue(FlowPointsProperty);
        set => SetValue(FlowPointsProperty, value);
    }
    public static readonly DependencyProperty FlowPointsProperty =
        DependencyProperty.Register(nameof(FlowPoints), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnFlowPointsChanged));

    private static void OnFlowPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._flowHandler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._flowHandler;
        c.SyncSeries(c._flowSeries, np);
    }

    public ObservableCollection<double>? TempPoints
    {
        get => (ObservableCollection<double>?)GetValue(TempPointsProperty);
        set => SetValue(TempPointsProperty, value);
    }
    public static readonly DependencyProperty TempPointsProperty =
        DependencyProperty.Register(nameof(TempPoints), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnTempPointsChanged));

    private static void OnTempPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._tempHandler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._tempHandler;
        c.SyncSeries(c._tempSeries, np);
    }

    public ObservableCollection<double>? PrimaryPoints
    {
        get => (ObservableCollection<double>?)GetValue(PrimaryPointsProperty);
        set => SetValue(PrimaryPointsProperty, value);
    }
    public static readonly DependencyProperty PrimaryPointsProperty =
        DependencyProperty.Register(nameof(PrimaryPoints), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnPrimaryPointsChanged));

    private static void OnPrimaryPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._primaryHandler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._primaryHandler;
        c.SyncSeries(c._primarySeries, np);
    }

    private void SyncSeries(LineSeries series, ObservableCollection<double>? points)
    {
        series.Points.Clear();
        if (points == null || points.Count == 0) { _plotView.InvalidatePlot(true); return; }
        for (int i = 0; i < points.Count; i++) series.Points.Add(new DataPoint(i, points[i]));
        AutoScaleYAxis();
        _plotView.InvalidatePlot(true);
    }

    private void AutoScaleYAxis()
    {
        double min = double.MaxValue, max = double.MinValue; bool hasData = false;
        foreach (var s in new LineSeries[] { _pressureSeries, _flowSeries, _tempSeries, _primarySeries })
        {
            if (!s.IsVisible || s.Points.Count == 0) continue;
            hasData = true;
            foreach (var pt in s.Points) { if (double.IsNaN(pt.Y) || double.IsInfinity(pt.Y)) continue; if (pt.Y < min) min = pt.Y; if (pt.Y > max) max = pt.Y; }
        }
        if (!hasData || min > max) return;
        double range = max - min; if (range == 0) range = 1;
        _yAxis.Minimum = min - range * 0.1; _yAxis.Maximum = max + range * 0.1;
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _plotView.CaptureMouse();
            _trackerBorder.Visibility = Visibility.Collapsed;
            _trackerLine.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void OnMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (_isPanning) { _isPanning = false; _plotView.ReleaseMouseCapture(); }
    }

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(_plotView);
        var pa = _model.PlotArea;
        if (pa.Width <= 0) return;
        double mouseX = _xAxis.InverseTransform(pos.X);
        double zf = e.Delta > 0 ? 0.85 : 1.18;
        _xAxis.Zoom(mouseX - (mouseX - _xAxis.ActualMinimum) * zf, mouseX + (_xAxis.ActualMaximum - mouseX) * zf);
        _plotView.InvalidatePlot(false);
        e.Handled = true;
    }
}

public enum DisplayMode { ThreeChannel, SingleChannel }
