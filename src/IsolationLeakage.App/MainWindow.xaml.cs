using System;
using System.Windows;
using System.Windows.Controls;

namespace IsolationLeakage.App;

public partial class MainWindow : Window
{
    // 界面设计基准尺寸（DIP）：客户区达到此尺寸即按 1:1 显示；
    // 更小（如高分屏 200% 缩放的小笔记本）则整体按比例缩小到刚好装下。
    private const double DesignWidth = 1366;
    private const double DesignHeight = 768;
    private const double MinScale = 0.5;

    public MainWindow()
    {
        InitializeComponent();
        // 数据库连接状态已在首页概览顶部显示，无需弹窗
    }

    /// <summary>
    /// 根据缩放宿主的真实客户区尺寸，把整套界面缩放到刚好装下。
    /// 解决高分屏（如 200% 缩放）小屏笔记本上窗口过大、内容溢出屏幕的问题。
    /// </summary>
    private void ScaleHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid host || RootScale == null) return;

        double availW = host.ActualWidth;
        double availH = host.ActualHeight;
        if (availW <= 0 || availH <= 0) return;

        // 取宽高两个方向所需缩放的较小值；不超过 1（大屏不放大），不低于下限
        double scale = Math.Min(availW / DesignWidth, availH / DesignHeight);
        scale = Math.Max(MinScale, Math.Min(1.0, scale));

        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
    }
}
