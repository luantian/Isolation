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
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 工业风格趋势图组件（基于 OxyPlot 封装）
/// 支持 5 通道（压力P1 / 流量M1 / 流量M2 / 温度T / 压力P2）+ 真实时间轴。
/// 自定义 Tracker 浮层：固定尺寸、鼠标悬停显示各通道值。
/// </summary>
public class TrendChart : ContentControl, IDisposable
{
    private static readonly OxyColor ColorPressure = OxyColor.FromRgb(0x07, 0x58, 0xD8);  // P1 蓝
    private static readonly OxyColor ColorFlow = OxyColor.FromRgb(0x12, 0xA3, 0x66);       // M1 绿
    private static readonly OxyColor ColorTemp = OxyColor.FromRgb(0xF9, 0x73, 0x16);       // T  橙
    private static readonly OxyColor ColorFlow2 = OxyColor.FromRgb(0x0E, 0xA5, 0xE9);      // M2 青
    private static readonly OxyColor ColorPressure2 = OxyColor.FromRgb(0x8B, 0x5C, 0xF6);  // P2 紫
    private static readonly OxyColor ColorPrimary = OxyColor.FromRgb(0x07, 0x58, 0xD8);
    private static readonly OxyColor ColorGrid = OxyColor.FromArgb(0x40, 0xDE, 0xE4, 0xEE);

    private static readonly SolidColorBrush WpfPressure = new(Color.FromRgb(0x07, 0x58, 0xD8));
    private static readonly SolidColorBrush WpfFlow = new(Color.FromRgb(0x12, 0xA3, 0x66));
    private static readonly SolidColorBrush WpfTemp = new(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly SolidColorBrush WpfFlow2 = new(Color.FromRgb(0x0E, 0xA5, 0xE9));
    private static readonly SolidColorBrush WpfPressure2 = new(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly SolidColorBrush WpfPrimary = new(Color.FromRgb(0x07, 0x58, 0xD8));

    // Tracker 固定尺寸（5 行需要更高）
    private const double TrackerWidth = 190;
    private const double TrackerHeight = 168;

    private readonly OxyPlot.Wpf.PlotView _plotView;
    private readonly PlotModel _model;
    private readonly LineSeries _pressureSeries;
    private readonly LineSeries _flowSeries;
    private readonly LineSeries _tempSeries;
    private readonly LineSeries _flow2Series;
    private readonly LineSeries _pressure2Series;
    private readonly LineSeries _primarySeries;
    private readonly LinearAxis _xAxis;
    private readonly LinearAxis _yAxis;
    private readonly LinearAxis _yAxisSecondary;
    private DisplayMode _mode;

    private bool _isPanning;
    private bool _disposed;

    // 视口跟随由 AutoScroll 依赖属性控制（勾选=跟随最新，取消=停在当前视口，用户拖到哪是哪）。
    // 用户拖拽/缩放（右键平移、滚轮缩放）通过 X 轴 AxisChanged 检测并自动取消勾选。
    // _suppressAxisChanged：包裹“程序主动改视口”的调用，避免把自身滚动误判为用户操作。
    private bool _suppressAxisChanged;

    // 共享时间轴值（秒偏移）。为空时 X 轴退回到采样索引。
    // 容量按需倍增（_timeValuesCount 为逻辑长度）：实时每 tick 追加摊还 O(1)，
    // 避免原先每次 Add 都整组拷贝新数组（86,400 点长会话累计 O(n²) 分配）。
    private double[] _timeValues = [];
    private int _timeValuesCount;

    // Y 轴极值增量缓存：避免每秒全量扫描 86,400 个点。
    // 新增点时仅比较新值，Reset/Replace 时重置为 MaxValue/MinValue 并触发全量扫描。
    private double _cachedYMin = double.MaxValue;
    private double _cachedYMax = double.MinValue;

    // ===== 动态通道模式：每个 TrendChannel 一条 series，运行时增删 =====
    private readonly Dictionary<TrendChannel, LineSeries> _dynamicSeries = new();
    private readonly Dictionary<ObservableCollection<double>, TrendChannel> _pointsToChannel = new();
    private bool _dynamicMode;

    // Tracker 浮层
    private readonly Border _trackerBorder;
    private readonly TextBlock _tbHeader;
    private readonly StackPanel _sp1;
    private readonly StackPanel _sp2;
    private readonly StackPanel _sp3;
    private readonly StackPanel _sp4;
    private readonly StackPanel _sp5;
    private readonly Line _trackerLine;
    private readonly Grid _overlayGrid;

    public TrendChart()
    {
        _model = new PlotModel
        {
            PlotAreaBackground = OxyColors.Transparent,
            PlotAreaBorderColor = OxyColors.Transparent,
            Padding = new OxyThickness(4, 4, 12, 48),
        };

        _xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom, Title = "时间 (s)",
            TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            AxislineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
            TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
            MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = ColorGrid,
            MajorGridlineThickness = 1, MinorGridlineStyle = LineStyle.None,
            IsZoomEnabled = true, IsPanEnabled = true,
        };
        _xAxis.AxisChanged += OnXAxisChanged;
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

        _pressureSeries = CreateSeries("压力P1 (MPa)", ColorPressure);
        _model.Series.Add(_pressureSeries);
        _flowSeries = CreateSeries("流量M1 (Nml/min)", ColorFlow);
        _model.Series.Add(_flowSeries);
        _tempSeries = CreateSeries("温度T (℃)", ColorTemp);
        _model.Series.Add(_tempSeries);
        _flow2Series = CreateSeries("流量M2 (Nml/min)", ColorFlow2);
        _model.Series.Add(_flow2Series);
        _pressure2Series = CreateSeries("压力P2 (MPa)", ColorPressure2);
        _model.Series.Add(_pressure2Series);
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

        _sp1 = CreateValueRow(WpfPressure, out _);
        _sp2 = CreateValueRow(WpfFlow, out _);
        _sp3 = CreateValueRow(WpfTemp, out _);
        _sp4 = CreateValueRow(WpfFlow2, out _);
        _sp5 = CreateValueRow(WpfPressure2, out _);
        sp.Children.Add(_sp1);
        sp.Children.Add(_sp2);
        sp.Children.Add(_sp3);
        sp.Children.Add(_sp4);
        sp.Children.Add(_sp5);

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
        _plotView = new OxyPlot.Wpf.PlotView
        {
            Model = _model,
            IsMouseWheelEnabled = false  // 禁用 OxyPlot 默认滚轮缩放，使用自定义 Y 轴缩放
        };

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

        // 控件卸载时退订集合事件（ViewModel 比控件活得久，不退订会泄漏控件）。
        // 注意：不做破坏性清理——TabControl 切走再切回复用的是同一控件实例，
        // 清空 Model/Series 且不可恢复会让图表永久空白；改为"停用"，Loaded 时重新挂接。
        Unloaded += (_, _) => DetachBindings();
        Loaded += (_, _) => ReattachBindings();

        // 实时数据增量更新：不重置缩放，不强制全量重绘
        // 集合内容变化（实时增量 Add / ReplaceAll）时，把数据同步进对应 series 再重绘。
        // 注意：必须重建 series.Points，否则只 InvalidatePlot 画的是空曲线。
        _pressureHandler = (_, e) => OnChannelCollectionChanged(_pressureSeries, PressurePoints, e);
        _flowHandler = (_, e) => OnChannelCollectionChanged(_flowSeries, FlowPoints, e);
        _tempHandler = (_, e) => OnChannelCollectionChanged(_tempSeries, TempPoints, e);
        _flow2Handler = (_, e) => OnChannelCollectionChanged(_flow2Series, Flow2Points, e);
        _pressure2Handler = (_, e) => OnChannelCollectionChanged(_pressure2Series, Pressure2Points, e);
        _primaryHandler = (_, e) => OnChannelCollectionChanged(_primarySeries, PrimaryPoints, e);
        _timeHandler = (_, e) => OnTimeCollectionChanged(e);
        _channelsHandler = OnChannelsCollectionChanged;
        _dynamicPointsHandler = OnDynamicPointsChanged;
        _channelPropertyHandler = OnChannelPropertyChanged;
    }

    /// <summary>动态通道属性变化：IsVisible 改变时切换对应曲线可见并重绘。</summary>
    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrendChannel.IsVisible)) return;
        if (sender is not TrendChannel ch) return;
        if (!_dynamicSeries.TryGetValue(ch, out var series)) return;

        series.IsVisible = ch.IsVisible;
        AutoScaleYAxis();
        _plotView.InvalidatePlot(false);
    }

    /// <summary>集合内容变化时重建该通道 series 并重绘（实时滚动：X 轴跟随最新数据，Y 轴自动适配）。</summary>
    private void OnChannelCollectionChanged(LineSeries series, ObservableCollection<double>? points, NotifyCollectionChangedEventArgs? e = null)
    {
        // 增量模式：单点 Add 时仅追加新点，避免全量重建
        if (e is { Action: NotifyCollectionChangedAction.Add } && e.NewItems != null && points != null)
        {
            int baseIndex = e.NewStartingIndex;
            foreach (double val in e.NewItems)
            {
                double x = baseIndex < _timeValuesCount ? _timeValues[baseIndex] : baseIndex;
                series.Points.Add(new DataPoint(x, val));
                if (!double.IsNaN(val) && !double.IsInfinity(val))
                {
                    if (val < _cachedYMin) _cachedYMin = val;
                    if (val > _cachedYMax) _cachedYMax = val;
                }
                baseIndex++;
            }
        }
        else
        {
            RebuildSeriesX(series, points);
            _cachedYMin = double.MaxValue;
            _cachedYMax = double.MinValue;
        }

        // X 轴：勾选”自动”才跟随最新数据滚动视口；取消勾选时停在当前视口（用户拖到哪是哪）。
        if (AutoScroll)
        {
            ScrollXAxisToLatest();
            // Y 轴：增量模式下使用缓存，Reset 时全量扫描
            if (e?.Action == NotifyCollectionChangedAction.Add)
                ApplyCachedYAxis();
            else
                AutoScaleYAxis();
        }

        _plotView.InvalidatePlot(false);
    }

    /// <summary>最大显示时长（秒）。为 0 时显示全部数据；大于 0 时只显示最近 N 秒。</summary>
    public double MaxDisplaySeconds
    {
        get => (double)GetValue(MaxDisplaySecondsProperty);
        set => SetValue(MaxDisplaySecondsProperty, value);
    }
    public static readonly DependencyProperty MaxDisplaySecondsProperty =
        DependencyProperty.Register(nameof(MaxDisplaySeconds), typeof(double), typeof(TrendChart),
            new FrameworkPropertyMetadata(0.0, OnMaxDisplaySecondsChanged));

    /// <summary>显示时长变化：跟随状态下立即按新窗口重新对齐视口（停止监视时也生效）。</summary>
    private static void OnMaxDisplaySecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (!c.AutoScroll) return;   // 手动模式：不打扰用户当前视口
        c.ScrollXAxisToLatest();
        c.AutoScaleYAxis();
        c._plotView.InvalidatePlot(false);
    }

    /// <summary>
    /// 是否自动跟随最新数据点滚动视口。
    /// 勾选=新数据进来时视口滚到最新窗口；取消=视口停在用户拖拽/缩放的位置，数据仍全量保留。
    /// </summary>
    public bool AutoScroll
    {
        get => (bool)GetValue(AutoScrollProperty);
        set => SetValue(AutoScrollProperty, value);
    }
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.Register(nameof(AutoScroll), typeof(bool), typeof(TrendChart),
            new FrameworkPropertyMetadata(true, OnAutoScrollChanged));

    /// <summary>
    /// 底部 padding（用于覆盖默认值 48px，实时监视页面可设为 16px 以填满底部）
    /// </summary>
    public double BottomPadding
    {
        get => (double)GetValue(BottomPaddingProperty);
        set => SetValue(BottomPaddingProperty, value);
    }
    public static readonly DependencyProperty BottomPaddingProperty =
        DependencyProperty.Register(nameof(BottomPadding), typeof(double), typeof(TrendChart),
            new FrameworkPropertyMetadata(48.0, OnBottomPaddingChanged));

    private static void OnBottomPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        var newBottom = (double)e.NewValue;
        c._model.Padding = new OxyThickness(4, 4, 12, newBottom);
        c._plotView.InvalidatePlot(true);
    }

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        // 重新勾选”自动”时，立即把视口对齐到最新窗口；取消时保持当前视口不动。
        if (e.NewValue is true)
        {
            c.ScrollXAxisToLatest();
            c.AutoScaleYAxis();
            // 强制刷新图表，确保 Y 轴范围立即生效
            c._plotView.InvalidatePlot(true);
        }
    }

    /// <summary>
    /// X 轴视口被改变时的回调。若不是程序主动滚动（_suppressAxisChanged=false），
    /// 说明是用户右键平移或滚轮缩放，自动取消“自动”跟随，让视口停在用户操作的位置。
    /// </summary>
    private void OnXAxisChanged(object? sender, AxisChangedEventArgs e)
    {
        if (_suppressAxisChanged) return;
        if (AutoScroll) AutoScroll = false;
    }

    /// <summary>实时模式：把 X 轴范围设为当前数据宽度，使新数据始终可见、旧数据滚出。</summary>
    private void ScrollXAxisToLatest()
    {
        // 取可见 series 里最长的点数作为当前数据宽度
        int maxCount = 0;
        foreach (var s in VisibleSeries())
        {
            if (s.Points.Count > maxCount) maxCount = s.Points.Count;
        }
        if (maxCount <= 1) return;

        // 包裹程序主动缩放：避免触发的 AxisChanged 被误判为用户操作而取消“自动”。
        _suppressAxisChanged = true;
        try
        {
            if (_timeValuesCount > 0)
            {
                // 有真实时间轴：按时间显示最近窗口
                double xMax = _timeValues[Math.Min(_timeValuesCount, maxCount) - 1];
                double xMin = _timeValues[0];

                // 如果设置了 MaxDisplaySeconds > 0，只显示最近 N 秒
                if (MaxDisplaySeconds > 0)
                {
                    double windowStart = xMax - MaxDisplaySeconds;
                    // 时间轴单调递增：二分查找第一个 >= windowStart 的时间点（替代每 tick 线性扫描）
                    int lo = 0, hi = _timeValuesCount;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (_timeValues[mid] < windowStart) lo = mid + 1; else hi = mid;
                    }
                    if (lo < _timeValuesCount) xMin = _timeValues[lo];
                }

                _xAxis.Zoom(xMin, xMax);
            }
            else
            {
                // 索引轴：0 .. 当前点数
                _xAxis.Zoom(0, maxCount - 1);
            }
        }
        finally
        {
            _suppressAxisChanged = false;
        }
    }

    private readonly NotifyCollectionChangedEventHandler _pressureHandler;
    private readonly NotifyCollectionChangedEventHandler _flowHandler;
    private readonly NotifyCollectionChangedEventHandler _tempHandler;
    private readonly NotifyCollectionChangedEventHandler _flow2Handler;
    private readonly NotifyCollectionChangedEventHandler _pressure2Handler;
    private readonly NotifyCollectionChangedEventHandler _primaryHandler;
    private readonly NotifyCollectionChangedEventHandler _timeHandler;
    private readonly NotifyCollectionChangedEventHandler _channelsHandler;
    private readonly NotifyCollectionChangedEventHandler _dynamicPointsHandler;
    private readonly System.ComponentModel.PropertyChangedEventHandler _channelPropertyHandler;

    private static LineSeries CreateSeries(string title, OxyColor color)
    {
        return new LineSeries { Title = title, Color = color, StrokeThickness = 1.5, MarkerType = MarkerType.None };
    }

    private static StackPanel CreateValueRow(SolidColorBrush colorBrush, out TextBlock valBlock)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        sp.Children.Add(new TextBlock { Text = "●", Foreground = colorBrush, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        var lbl = new TextBlock { Width = 58, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), FontSize = 13, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        sp.Children.Add(lbl);
        valBlock = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = System.Windows.FontWeights.Medium, FontFamily = new FontFamily("Consolas"), VerticalAlignment = System.Windows.VerticalAlignment.Center };
        sp.Children.Add(valBlock);
        return sp;
    }

    public void ResetZoom()
    {
        _suppressAxisChanged = true;
        try { _xAxis.Reset(); }
        finally { _suppressAxisChanged = false; }
        AutoScaleYAxis();
    }

    #region Tracker

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_isPanning) return;

        // 动态通道模式下不显示固定 5 行 Tracker 浮层（通道数不固定）
        if (_dynamicMode)
        {
            _trackerBorder.Visibility = Visibility.Collapsed;
            _trackerLine.Visibility = Visibility.Collapsed;
            return;
        }

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
        int idx = NearestIndex(dataX);
        if (idx < 0)
        {
            _trackerBorder.Visibility = Visibility.Collapsed;
            _trackerLine.Visibility = Visibility.Collapsed;
            return;
        }

        // 表头：有时间轴显示时间，否则显示采样点
        _tbHeader.Text = _timeValuesCount > idx
            ? $"时间: {_timeValues[idx]:0.#}s"
            : $"采样点: {idx}";

        SetRowVisible(_sp1, "压力P1", _pressureSeries, idx);
        SetRowVisible(_sp2, "流量M1", _flowSeries, idx);
        SetRowVisible(_sp3, "温度T", _tempSeries, idx);
        SetRowVisible(_sp4, "流量M2", _flow2Series, idx);
        SetRowVisible(_sp5, "压力P2", _pressure2Series, idx);

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

    /// <summary>
    /// 根据数据坐标 X 找最近的数据点索引。
    /// 有时间轴时按时间值二分/线性查找；否则 X 即索引，直接四舍五入。
    /// </summary>
    private int NearestIndex(double dataX)
    {
        if (_timeValuesCount == 0)
        {
            int idx = (int)Math.Round(dataX);
            return idx < 0 ? -1 : idx;
        }

        // 时间轴单调递增：二分查找最近的时间点 O(log n)（原线性扫描在长会话下每 tick O(n)）
        if (dataX <= _timeValues[0]) return 0;
        int hiIdx = _timeValuesCount - 1;
        if (dataX >= _timeValues[hiIdx]) return hiIdx;
        int lo = 0;
        while (hiIdx - lo > 1)
        {
            int mid = (lo + hiIdx) / 2;
            if (_timeValues[mid] <= dataX) lo = mid; else hiIdx = mid;
        }
        return (dataX - _timeValues[lo]) <= (_timeValues[hiIdx] - dataX) ? lo : hiIdx;
    }

    private static void SetRowVisible(StackPanel sp, string label, LineSeries series, int idx)
    {
        if (series.Points.Count > idx && idx >= 0 && series.IsVisible)
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
            bool multi = value is DisplayMode.ThreeChannel or DisplayMode.FiveChannel;
            _pressureSeries.IsVisible = multi;
            _flowSeries.IsVisible = multi;
            _tempSeries.IsVisible = multi;
            // M2/P2 仅在 5 通道模式显示
            _flow2Series.IsVisible = value == DisplayMode.FiveChannel;
            _pressure2Series.IsVisible = value == DisplayMode.FiveChannel;
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

    /// <summary>
    /// 动态通道集合（实时监控用）。设置后进入动态模式：每个 TrendChannel 一条曲线，
    /// 集合增删时曲线随之增删；隐藏固定的 5 通道 series。
    /// </summary>
    public ObservableCollection<TrendChannel>? Channels
    {
        get => (ObservableCollection<TrendChannel>?)GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }
    public static readonly DependencyProperty ChannelsProperty =
        DependencyProperty.Register(nameof(Channels), typeof(ObservableCollection<TrendChannel>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnChannelsChanged));

    private static void OnChannelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<TrendChannel> oldC)
            oldC.CollectionChanged -= c._channelsHandler;
        var np = e.NewValue as ObservableCollection<TrendChannel>;
        if (np != null) np.CollectionChanged += c._channelsHandler;
        c.RebuildDynamicChannels();
    }

    /// <summary>动态通道集合增删时，重建所有动态 series。</summary>
    private void OnChannelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildDynamicChannels();

    /// <summary>根据 Channels 集合重建动态 series（每通道一条曲线），并切到动态模式。</summary>
    private void RebuildDynamicChannels()
    {
        // 解除旧动态 series 的数据订阅
        foreach (var kv in _dynamicSeries)
        {
            _model.Series.Remove(kv.Value);
        }
        foreach (var ch in _pointsToChannel.Values.ToList())
            ch.Points.CollectionChanged -= _dynamicPointsHandler;
        foreach (var ch in _dynamicSeries.Keys.ToList())
            ch.PropertyChanged -= _channelPropertyHandler;
        _dynamicSeries.Clear();
        _pointsToChannel.Clear();

        var channels = Channels;
        if (channels == null || channels.Count == 0)
        {
            _dynamicMode = false;
            _plotView.InvalidatePlot(true);
            return;
        }

        _dynamicMode = true;
        // 隐藏固定通道
        _pressureSeries.IsVisible = false;
        _flowSeries.IsVisible = false;
        _tempSeries.IsVisible = false;
        _flow2Series.IsVisible = false;
        _pressure2Series.IsVisible = false;
        _primarySeries.IsVisible = false;

        foreach (var ch in channels)
        {
            var series = CreateSeries(string.IsNullOrEmpty(ch.Unit) ? ch.Name : $"{ch.Name} ({ch.Unit})",
                OxyColor.FromArgb(ch.Color.A, ch.Color.R, ch.Color.G, ch.Color.B));
            series.IsVisible = ch.IsVisible;   // 尊重通道显隐开关
            _model.Series.Add(series);
            _dynamicSeries[ch] = series;
            _pointsToChannel[ch.Points] = ch;
            ch.Points.CollectionChanged += _dynamicPointsHandler;
            ch.PropertyChanged += _channelPropertyHandler;   // 监听 IsVisible 变化
            RebuildSeriesX(series, ch.Points);
        }

        // 首次构建通道：跟随状态下对齐到最新窗口
        if (AutoScroll)
        {
            ScrollXAxisToLatest();
        }
        AutoScaleYAxis();
        _plotView.InvalidatePlot(true);
    }

    /// <summary>某个动态通道的数据点变化时，增量更新该通道 series 并滚动重绘。</summary>
    private void OnDynamicPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not ObservableCollection<double> pts) return;
        if (!_pointsToChannel.TryGetValue(pts, out var ch)) return;
        if (!_dynamicSeries.TryGetValue(ch, out var series)) return;

        // 增量处理：仅对 Add 操作追加新点，避免 86,400 点全量重建
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            int baseIndex = e.NewStartingIndex;
            foreach (double val in e.NewItems)
            {
                double x = baseIndex < _timeValuesCount ? _timeValues[baseIndex] : baseIndex;
                series.Points.Add(new DataPoint(x, val));
                baseIndex++;
            }
            // 增量更新 Y 轴极值缓存（只检查新增点）
            foreach (double val in e.NewItems)
            {
                if (!double.IsNaN(val) && !double.IsInfinity(val))
                {
                    if (val < _cachedYMin) _cachedYMin = val;
                    if (val > _cachedYMax) _cachedYMax = val;
                }
            }
        }
        else
        {
            // Reset/Replace/Remove → 全量重建
            RebuildSeriesX(series, pts);
            _cachedYMin = double.MaxValue;
            _cachedYMax = double.MinValue;
        }

        // 整体替换（加载新数据集 / 历史回放按窗口裁剪）时始终重新贴合视口；
        // 实时逐点追加(Add)时，只有勾选”自动”才跟随最新，否则停在用户拖拽的视口。
        bool bulkReplace = e.Action == NotifyCollectionChangedAction.Reset;
        if (AutoScroll || bulkReplace)
        {
            ScrollXAxisToLatest();
            // Y 轴：只有”自动”模式或整体替换时才自动缩放（增量模式下使用缓存）
            if (bulkReplace)
                AutoScaleYAxis();
            else
                ApplyCachedYAxis();
        }
        _plotView.InvalidatePlot(false);
    }

    /// <summary>
    /// 时间轴数据（秒偏移）。设置后各通道按真实时间绘制 X 轴；为空则用采样索引。
    /// </summary>
    public ObservableCollection<double>? TimePoints
    {
        get => (ObservableCollection<double>?)GetValue(TimePointsProperty);
        set => SetValue(TimePointsProperty, value);
    }
    public static readonly DependencyProperty TimePointsProperty =
        DependencyProperty.Register(nameof(TimePoints), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnTimePointsChanged));

    private static void OnTimePointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> oldT) oldT.CollectionChanged -= c._timeHandler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._timeHandler;
        c.SetTimeValues(np?.ToArray() ?? []);
        c._xAxis.Title = c._timeValuesCount > 0 ? "时间 (s)" : "时间 (s)";
        // 重新绑定新时间轴（加载新数据集）：始终贴合视口
        c.ResyncAll(forceFit: true);
        c._plotView.InvalidatePlot(true);
    }

    /// <summary>整体替换内部时间值数组（重绑定 / Reset 全量重建路径）。</summary>
    private void SetTimeValues(double[] values)
    {
        _timeValues = values;
        _timeValuesCount = values.Length;
    }

    /// <summary>增量追加时间值：容量不足时倍增扩容（摊还 O(1)），不再每次 Add 整组拷贝。</summary>
    private void AppendTimeValues(System.Collections.IList newItems)
    {
        int addCount = newItems.Count;
        if (_timeValues.Length < _timeValuesCount + addCount)
        {
            int newCap = Math.Max(16, _timeValues.Length * 2);
            while (newCap < _timeValuesCount + addCount) newCap *= 2;
            Array.Resize(ref _timeValues, newCap);
        }
        foreach (double t in newItems)
        {
            _timeValues[_timeValuesCount++] = t;
        }
    }

    /// <summary>时间轴集合内容变化（实时每 tick 追加序号）时，增量刷新内部 X 值数组。</summary>
    private void OnTimeCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        // 增量模式：单点 Add 时仅追加新时间值，避免每秒 86,400 次 ToArray
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            AppendTimeValues(e.NewItems);

            // 各通道 series 也只需追加新 X 点（Y 已在 OnDynamicPointsChanged 中追加）
            foreach (var (ch, series) in _dynamicSeries)
            {
                int baseIndex = e.NewStartingIndex;
                foreach (double val in ch.Points.Skip(e.NewStartingIndex).Take(e.NewItems.Count))
                {
                    double x = baseIndex < _timeValuesCount ? _timeValues[baseIndex] : baseIndex;
                    series.Points.Add(new DataPoint(x, val));
                    baseIndex++;
                }
            }

            // 固定通道也需要追加对应的 X 点
            AppendTimePointToFixedSeries(e.NewStartingIndex, e.NewItems.Count);

            if (AutoScroll) ScrollXAxisToLatest();
            _plotView.InvalidatePlot(false);
            return;
        }

        // Reset/Replace/Remove → 全量重建
        SetTimeValues(TimePoints?.ToArray() ?? []);
        _cachedYMin = double.MaxValue;
        _cachedYMax = double.MinValue;
        ResyncAll();
    }

    /// <summary>固定通道增量追加新的时间-X 点</summary>
    private void AppendTimePointToFixedSeries(int startIndex, int count)
    {
        AppendTimeToSeries(_pressureSeries, PressurePoints, startIndex, count);
        AppendTimeToSeries(_flowSeries, FlowPoints, startIndex, count);
        AppendTimeToSeries(_tempSeries, TempPoints, startIndex, count);
        AppendTimeToSeries(_flow2Series, Flow2Points, startIndex, count);
        AppendTimeToSeries(_pressure2Series, Pressure2Points, startIndex, count);
        AppendTimeToSeries(_primarySeries, PrimaryPoints, startIndex, count);
    }

    private void AppendTimeToSeries(LineSeries series, ObservableCollection<double>? points, int startIndex, int count)
    {
        if (points == null) return;
        for (int i = 0; i < count && (startIndex + i) < points.Count; i++)
        {
            int idx = startIndex + i;
            double x = idx < _timeValuesCount ? _timeValues[idx] : idx;
            series.Points.Add(new DataPoint(x, points[idx]));
        }
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

    public ObservableCollection<double>? Flow2Points
    {
        get => (ObservableCollection<double>?)GetValue(Flow2PointsProperty);
        set => SetValue(Flow2PointsProperty, value);
    }
    public static readonly DependencyProperty Flow2PointsProperty =
        DependencyProperty.Register(nameof(Flow2Points), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnFlow2PointsChanged));

    private static void OnFlow2PointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._flow2Handler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._flow2Handler;
        c.SyncSeries(c._flow2Series, np);
    }

    public ObservableCollection<double>? Pressure2Points
    {
        get => (ObservableCollection<double>?)GetValue(Pressure2PointsProperty);
        set => SetValue(Pressure2PointsProperty, value);
    }
    public static readonly DependencyProperty Pressure2PointsProperty =
        DependencyProperty.Register(nameof(Pressure2Points), typeof(ObservableCollection<double>), typeof(TrendChart),
            new FrameworkPropertyMetadata(null, OnPressure2PointsChanged));

    private static void OnPressure2PointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TrendChart)d;
        if (e.OldValue is ObservableCollection<double> old) old.CollectionChanged -= c._pressure2Handler;
        var np = e.NewValue as ObservableCollection<double>;
        if (np != null) np.CollectionChanged += c._pressure2Handler;
        c.SyncSeries(c._pressure2Series, np);
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

    /// <summary>用一个数据集合刷新某条 series；X 轴用时间值（若有）否则用索引。</summary>
    private void SyncSeries(LineSeries series, ObservableCollection<double>? points)
    {
        series.Points.Clear();
        if (points == null || points.Count == 0) { _plotView.InvalidatePlot(true); return; }
        for (int i = 0; i < points.Count; i++)
        {
            double x = _timeValuesCount > i ? _timeValues[i] : i;
            series.Points.Add(new DataPoint(x, points[i]));
        }
        // 全量加载历史数据时自动缩放 Y 轴（实时增量更新时不缩放）
        ResetZoom();
        _plotView.InvalidatePlot(true);
    }

    /// <summary>时间轴变化后，重建所有已绑定通道的 X 坐标。
    /// forceFit=true（重新绑定新时间轴）时始终贴合视口；否则只有勾选“自动”才跟随最新。</summary>
    private void ResyncAll(bool forceFit = false)
    {
        // 重建固定通道（5通道模式）
        RebuildSeriesX(_pressureSeries, PressurePoints);
        RebuildSeriesX(_flowSeries, FlowPoints);
        RebuildSeriesX(_tempSeries, TempPoints);
        RebuildSeriesX(_flow2Series, Flow2Points);
        RebuildSeriesX(_pressure2Series, Pressure2Points);
        RebuildSeriesX(_primarySeries, PrimaryPoints);

        // 重建动态通道（实时监控模式）
        if (_dynamicMode)
        {
            foreach (var (ch, series) in _dynamicSeries)
            {
                RebuildSeriesX(series, ch.Points);
            }
        }

        if (AutoScroll || forceFit)
        {
            ScrollXAxisToLatest();
            AutoScaleYAxis();
        }
    }

    private void RebuildSeriesX(LineSeries series, ObservableCollection<double>? points)
    {
        series.Points.Clear();
        if (points == null || points.Count == 0) return;
        for (int i = 0; i < points.Count; i++)
        {
            double x = _timeValuesCount > i ? _timeValues[i] : i;
            series.Points.Add(new DataPoint(x, points[i]));
        }
    }

    private void AutoScaleYAxis()
    {
        double min = double.MaxValue, max = double.MinValue; bool hasData = false;

        // 始终按当前 X 轴可见窗口统计 Y 极值——所见即所标，避免历史尖峰撑爆 Y 轴导致当前数据看不清
        double xMin = _xAxis.ActualMinimum;
        double xMax = _xAxis.ActualMaximum;
        // 如果 X 轴尚未初始化（范围无效），退回到全量扫描
        bool xAxisValid = xMin < xMax;

        foreach (var s in VisibleSeries())
        {
            if (s.Points.Count == 0) continue;
            hasData = true;
            foreach (var pt in s.Points)
            {
                if (double.IsNaN(pt.Y) || double.IsInfinity(pt.Y)) continue;
                if (!xAxisValid || (pt.X >= xMin && pt.X <= xMax))
                {
                    if (pt.Y < min) min = pt.Y;
                    if (pt.Y > max) max = pt.Y;
                }
            }
        }
        // 更新增量缓存（下次新点 Add 时可 O(1) 比较）
        _cachedYMin = min;
        _cachedYMax = max;

        if (!hasData || min > max) return;
        double niceMax = ComputeNiceMax(max);
        _yAxis.Zoom(min, niceMax);
    }

    /// <summary>
    /// 增量模式：使用缓存的 Y 轴极值快速应用缩放，避免每秒全量扫描。
    /// 当新点的 Y 值未超出缓存范围时，不调用 Zoom（减少 OxyPlot 重算）。
    /// </summary>
    private void ApplyCachedYAxis()
    {
        if (_cachedYMin > _cachedYMax) return; // 无有效数据
        double niceMax = ComputeNiceMax(_cachedYMax);
        _yAxis.Zoom(_cachedYMin, niceMax);
    }

    /// <summary>
    /// 将数据最大值向上取整到一个"好看"的整数刻度：
    ///   22   → 30     （取整到十）
    ///   3656 → 3700   （取整到百）
    ///   15647 → 16000 （取整到千）
    /// </summary>
    private static double ComputeNiceMax(double max)
    {
        if (max <= 0) return 1;
        // magnitude = 最高位对应的量级（22→10，3656→1000，15647→10000）
        // step = magnitude / 10，即保留到最高位下一级的精度
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        double step = Math.Max(1.0, magnitude / 10.0);
        double niceMax = Math.Ceiling(max / step) * step;
        // 如果 max 刚好在整刻度上，再加一格确保数据不会贴着顶
        if (niceMax <= max) niceMax += step;
        return niceMax;
    }

    /// <summary>当前可见的所有 series（动态模式=动态通道；否则=固定通道）。</summary>
    private IEnumerable<LineSeries> VisibleSeries()
    {
        if (_dynamicMode)
            return _dynamicSeries.Values.Where(s => s.IsVisible);
        return new[] { _pressureSeries, _flowSeries, _tempSeries, _flow2Series, _pressure2Series, _primarySeries }
            .Where(s => s.IsVisible);
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            // 双击弹出放大窗口。必须在进入平移态之前拦截并 return，
            // 否则第二次按下会 CaptureMouse，放大窗打开后主图卡在平移态。
            // 放大窗口内的图表不再响应（防递归弹窗）。
            if (e.ClickCount == 2)
            {
                if (Window.GetWindow(this) is not ChartZoomWindow)
                {
                    e.Handled = true;
                    ChartZoomWindow.ShowFor(this);
                }
                return;
            }

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

        // 滚轮始终缩放 Y 轴（数值范围），X 轴通过拖拽平移
        double mouseY = _yAxis.InverseTransform(pos.Y);
        double zf = e.Delta > 0 ? 0.85 : 1.18;
        _yAxis.Zoom(mouseY - (mouseY - _yAxis.ActualMinimum) * zf, mouseY + (_yAxis.ActualMaximum - mouseY) * zf);

        _plotView.InvalidatePlot(false);
        e.Handled = true;
    }

    /// <summary>
    /// 释放资源（控件卸载时调用）
    /// </summary>
    /// <summary>绑定是否处于挂接状态（Unloaded 停用 / Loaded 恢复）。</summary>
    private bool _bindingsAttached = true;

    /// <summary>
    /// 停用：退订全部集合事件（防 ViewModel 存活时控件卸载导致泄漏）。
    /// 可逆——不清 Model/Series/Axes，Loaded 时经 <see cref="ReattachBindings"/> 恢复。
    /// </summary>
    private void DetachBindings()
    {
        if (!_bindingsAttached) return;
        _bindingsAttached = false;

        // 取消 X 轴事件订阅
        _xAxis.AxisChanged -= OnXAxisChanged;

        // 取消动态通道的事件订阅
        foreach (var kvp in _dynamicSeries)
        {
            var ch = kvp.Key;
            ch.Points.CollectionChanged -= _dynamicPointsHandler;
            ch.PropertyChanged -= _channelPropertyHandler;
        }
        _dynamicSeries.Clear();
        _pointsToChannel.Clear();

        // 取消固定通道的事件订阅
        if (PressurePoints != null) PressurePoints.CollectionChanged -= _pressureHandler;
        if (FlowPoints != null) FlowPoints.CollectionChanged -= _flowHandler;
        if (TempPoints != null) TempPoints.CollectionChanged -= _tempHandler;
        if (Flow2Points != null) Flow2Points.CollectionChanged -= _flow2Handler;
        if (Pressure2Points != null) Pressure2Points.CollectionChanged -= _pressure2Handler;
        if (PrimaryPoints != null) PrimaryPoints.CollectionChanged -= _primaryHandler;
        if (TimePoints != null) TimePoints.CollectionChanged -= _timeHandler;
        if (Channels != null) Channels.CollectionChanged -= _channelsHandler;
    }

    /// <summary>
    /// 恢复：重新订阅集合事件并全量重建各通道 series 数据。
    /// TabControl 页签切走（Unloaded→停用）再切回（Loaded→本方法）后图表照常显示；
    /// Loaded 可能随可视树重挂多次触发，靠 _bindingsAttached 防重复订阅。
    /// </summary>
    private void ReattachBindings()
    {
        if (_bindingsAttached) return;
        _bindingsAttached = true;

        _xAxis.AxisChanged += OnXAxisChanged;

        if (PressurePoints != null) PressurePoints.CollectionChanged += _pressureHandler;
        if (FlowPoints != null) FlowPoints.CollectionChanged += _flowHandler;
        if (TempPoints != null) TempPoints.CollectionChanged += _tempHandler;
        if (Flow2Points != null) Flow2Points.CollectionChanged += _flow2Handler;
        if (Pressure2Points != null) Pressure2Points.CollectionChanged += _pressure2Handler;
        if (PrimaryPoints != null) PrimaryPoints.CollectionChanged += _primaryHandler;
        if (TimePoints != null) TimePoints.CollectionChanged += _timeHandler;
        if (Channels != null) Channels.CollectionChanged += _channelsHandler;

        // 时间轴先重建（_timeValues），各通道 series 的 X 值依赖它
        OnTimeCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        // 全量重建固定通道 series（等效一次 Reset）
        OnChannelCollectionChanged(_pressureSeries, PressurePoints, null);
        OnChannelCollectionChanged(_flowSeries, FlowPoints, null);
        OnChannelCollectionChanged(_tempSeries, TempPoints, null);
        OnChannelCollectionChanged(_flow2Series, Flow2Points, null);
        OnChannelCollectionChanged(_pressure2Series, Pressure2Points, null);
        OnChannelCollectionChanged(_primarySeries, PrimaryPoints, null);

        // 重建动态通道 series 与其订阅
        RebuildDynamicChannels();

        if (AutoScroll) ScrollXAxisToLatest();
        AutoScaleYAxis();
        _plotView.InvalidatePlot(true);
    }

    /// <summary>
    /// 真正销毁（控件不再复用时使用）。当前无外部调用方——
    /// 导航切换由 DataTemplate 重建整个视图，控件随可视树丢弃，Unloaded 停用退订已足够防泄漏。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachBindings();

        // OxyPlot 的 PlotModel 和 PlotView 没有 Dispose 方法，由 GC 自动回收
        _model.Series.Clear();
        _model.Axes.Clear();
        _plotView.Model = null;
    }
}

public enum DisplayMode { ThreeChannel, FiveChannel, SingleChannel }
