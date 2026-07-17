using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App;

public partial class MainWindow : Window
{
    // 界面设计基准尺寸（DIP）：客户区达到此尺寸即按 1:1 显示；
    // 更小（如高分屏 200% 缩放的小笔记本）则整体按比例缩小到刚好装下。
    private const double DesignWidth = 1366;
    private const double DesignHeight = 768;
    private const double MinScale = 0.5;

    // 保存事件处理程序引用，用于取消订阅
    private System.ComponentModel.PropertyChangedEventHandler? _propertyChangedHandler;
    private Action<Services.DatabaseFailoverService.DatabaseRole>? _roleChangedHandler;
    private Action<Services.DatabaseFailoverService.DatabaseStatus>? _statusChangedHandler;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 初始化数据库状态指示器
        UpdateDatabaseStatus();

        // 订阅故障切换服务的事件（保存引用以便后续取消订阅）
        var failoverService = DatabaseFailoverService.Instance;

        _propertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName is nameof(DatabaseFailoverService.CurrentRole)
                or nameof(DatabaseFailoverService.CurrentStatus)
                or nameof(DatabaseFailoverService.StatusMessage)
                or nameof(DatabaseFailoverService.CurrentServerDisplay))
            {
                Dispatcher.Invoke(UpdateDatabaseStatus);
            }
        };
        _roleChangedHandler = _ => Dispatcher.Invoke(UpdateDatabaseStatus);
        _statusChangedHandler = _ => Dispatcher.Invoke(UpdateDatabaseStatus);

        failoverService.PropertyChanged += _propertyChangedHandler;
        failoverService.RoleChanged += _roleChangedHandler;
        failoverService.StatusChanged += _statusChangedHandler;
    }

    /// <summary>
    /// 窗口关闭时释放资源
    /// </summary>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 取消订阅故障切换服务事件（防止内存泄漏）
        var failoverService = DatabaseFailoverService.Instance;
        if (_propertyChangedHandler != null)
            failoverService.PropertyChanged -= _propertyChangedHandler;
        if (_roleChangedHandler != null)
            failoverService.RoleChanged -= _roleChangedHandler;
        if (_statusChangedHandler != null)
            failoverService.StatusChanged -= _statusChangedHandler;

        // 释放 ViewModel 资源
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// 更新数据库状态指示器
    /// </summary>
    private void UpdateDatabaseStatus()
    {
        try
        {
            var service = DatabaseFailoverService.Instance;

            // 角色（主/从）
            DbRoleText.Text = service.CurrentRole == DatabaseFailoverService.DatabaseRole.Primary
                ? "主库" : "从库";

            // 状态文本和颜色
            var (statusText, statusColor) = service.CurrentStatus switch
            {
                DatabaseFailoverService.DatabaseStatus.Normal =>
                    ("正常", Color.FromRgb(0x16, 0xA3, 0x4A)),       // 绿色
                DatabaseFailoverService.DatabaseStatus.Checking =>
                    ("检测中", Color.FromRgb(0x25, 0x63, 0xEB)),      // 蓝色
                DatabaseFailoverService.DatabaseStatus.FailingOver =>
                    ("切换中", Color.FromRgb(0xD9, 0x77, 0x06)),      // 橙色
                DatabaseFailoverService.DatabaseStatus.OnSecondary =>
                    ("从库运行", Color.FromRgb(0xD9, 0x77, 0x06)),    // 橙色
                DatabaseFailoverService.DatabaseStatus.WaitingFailback =>
                    ("等待切回", Color.FromRgb(0x25, 0x63, 0xEB)),    // 蓝色
                DatabaseFailoverService.DatabaseStatus.Disabled =>
                    ("未启用", Color.FromRgb(0x64, 0x74, 0x8B)),      // 灰色
                _ => ("未知", Color.FromRgb(0x64, 0x74, 0x8B))
            };

            DbStatusText.Text = statusText;
            DbStatusDot.Fill = new SolidColorBrush(statusColor);
            DbServerText.Text = service.CurrentServerDisplay;
        }
        catch
        {
            // UI 更新不应导致崩溃
        }
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
