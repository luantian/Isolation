using System.Windows.Controls;
using System.Windows.Input;
using IsolationLeakage.App.Controls;

namespace IsolationLeakage.App.Views;

public partial class StatisticsAnalysisView : UserControl
{
    public StatisticsAnalysisView()
    {
        InitializeComponent();
    }

    /// <summary>双击 OxyPlot 图表弹出放大窗口（Tag 作为窗口标题；Model 借用-回还）。</summary>
    private void OnPlotDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is OxyPlot.Wpf.PlotView pv)
        {
            ChartZoomWindow.ShowFor(pv, pv.Tag as string ?? "图表放大");
            e.Handled = true;
        }
    }
}
