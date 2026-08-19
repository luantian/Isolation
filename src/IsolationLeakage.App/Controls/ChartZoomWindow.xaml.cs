using System.Windows;
using System.Windows.Input;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 图表放大窗口（双击图表弹出）。
/// - TrendChart 模式：内嵌新 TrendChart 并绑定与源相同的集合实例，实时数据自动同步刷新；
///   关闭时新图表 Unloaded→Dispose 退订全部集合事件，无泄漏。
/// - PlotModel 模式：OxyPlot 的 PlotModel 同一时刻只能挂一个 PlotView，
///   采用“借用-回还”：借用源 PlotView 的 Model，窗口关闭后归还（仅当源视图仍加载）。
/// </summary>
public partial class ChartZoomWindow : Window
{
    private OxyPlot.PlotModel? _borrowedModel;
    private OxyPlot.Wpf.PlotView? _lenderView;

    private ChartZoomWindow(string title)
    {
        InitializeComponent();
        Title = title;
        Width = SystemParameters.WorkArea.Width * 0.9;
        Height = SystemParameters.WorkArea.Height * 0.9;
    }

    /// <summary>放大显示一个 TrendChart。</summary>
    public static void ShowFor(TrendChart source)
    {
        var title = string.IsNullOrWhiteSpace(source.YAxisTitle) || source.YAxisTitle == "值"
            ? "图表放大"
            : $"{source.YAxisTitle} — 放大";
        var win = new ChartZoomWindow(title)
        {
            Owner = Window.GetWindow(source),
        };
        win.BuildTrendCopy(source);
        win.ShowDialog();
    }

    private void BuildTrendCopy(TrendChart source)
    {
        var chart = new TrendChart
        {
            YAxisTitle = source.YAxisTitle,
            MaxDisplaySeconds = source.MaxDisplaySeconds,
            AutoScroll = source.AutoScroll,
            BottomPadding = source.BottomPadding,
            Mode = source.Mode,
        };
        // 集合实例与源图表相同：双图表各自订阅/退订，互不影响
        chart.TimePoints = source.TimePoints;
        chart.PressurePoints = source.PressurePoints;
        chart.FlowPoints = source.FlowPoints;
        chart.TempPoints = source.TempPoints;
        chart.Flow2Points = source.Flow2Points;
        chart.Pressure2Points = source.Pressure2Points;
        chart.PrimaryPoints = source.PrimaryPoints;
        chart.Channels = source.Channels; // 最后设置：触发动态通道重建
        ChartHost.Content = chart;
    }

    /// <summary>放大显示一个 OxyPlot PlotView 的 Model（借用-回还）。</summary>
    public static void ShowFor(OxyPlot.Wpf.PlotView sourceView, string title)
    {
        var model = sourceView.Model;
        if (model is null) return;

        var win = new ChartZoomWindow(title)
        {
            Owner = Window.GetWindow(sourceView),
        };

        // 借用：同一 PlotModel 不能同时挂两个 PlotView
        sourceView.Model = null;
        win._borrowedModel = model;
        win._lenderView = sourceView;
        win.Closed += OnBorrowedClosed;

        win.ChartHost.Content = new OxyPlot.Wpf.PlotView { Model = model };
        win.ShowDialog();
    }

    /// <summary>归还借用的 PlotModel（仅当源 PlotView 仍在可视树上）。</summary>
    private static void OnBorrowedClosed(object? sender, System.EventArgs e)
    {
        if (sender is not ChartZoomWindow win) return;
        win.Closed -= OnBorrowedClosed;
        if (win._lenderView is { IsLoaded: true } pv && win._borrowedModel is { } m && pv.Model is null)
        {
            pv.Model = m;
        }
        win._borrowedModel = null;
        win._lenderView = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
