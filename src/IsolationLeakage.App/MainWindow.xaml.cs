using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

    // 会话超时监控：UserSession 30 分钟无操作自动失效，但此前无任何 UI 联动，
    // 超时后已加载页面数据持续可见且无恢复路径。每分钟检查一次，失效即强制回登录窗。
    private readonly DispatcherTimer _sessionTimer;
    private bool _reloginInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _sessionTimer.Tick += (_, _) =>
        {
            // IsLoggedIn 只检查超时、不刷新活动时间；会话失效（超时/登出）即回登录窗
            if (!Services.Security.UserSession.IsLoggedIn && !_reloginInProgress)
            {
                ReturnToLogin("由于长时间未操作，登录会话已超时。\n请重新登录以继续使用。");
            }
        };
        _sessionTimer.Start();
    }

    /// <summary>注销按钮：确认后清空会话并返回登录窗口。</summary>
    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            $"确定要注销当前用户【{Services.Security.UserSession.DisplayName}】吗？\n\n" +
            "注销后将返回登录窗口；正在进行的监视会先停止并保存数据。\n" +
            "（修改用户角色/权限后，需注销重新登录方可生效）",
            "注销确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        Services.Security.UserSession.Logout();
        ReturnToLogin("已注销当前用户。");
    }

    /// <summary>
    /// 返回登录窗口重新认证：
    /// 隐藏主窗 → 弹登录窗（模态）→ 成功则重建 ViewModel（按新会话刷新导航/权限）并重新显示；
    /// 取消登录则关闭主窗（App 挂接的 Closed → Shutdown 随之正常退出应用）。
    /// 也用于会话超时的强制重登。
    /// </summary>
    private void ReturnToLogin(string reason)
    {
        if (_reloginInProgress) return;
        _reloginInProgress = true;
        _sessionTimer.Stop();

        MessageBox.Show(this, reason, "会话结束", MessageBoxButton.OK, MessageBoxImage.Information);

        // 释放当前 ViewModel（停止各页定时器；实时监视若有会话会做兜底保存）
        try
        {
            if (DataContext is IDisposable disposable) disposable.Dispose();
        }
        catch
        {
            // 释放失败不阻塞重登
        }

        Hide();

        var loginWindow = new Views.Auth.LoginWindow { Owner = null };
        loginWindow.ShowDialog();

        if (Services.Security.UserSession.IsLoggedIn)
        {
            // 重新登录成功：按新会话重建 ViewModel（导航权限、用户名/角色显示随之刷新）
            DataContext = new ViewModels.MainViewModel();
            _sessionTimer.Start();
            Show();
            _reloginInProgress = false;
        }
        else
        {
            // 取消登录：关闭主窗 → App.Shutdown（与正常关窗一致）
            Close();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 防重复订阅：Loaded 可能随窗口重新挂载可视树多次触发，
        // 退订自身确保下面的事件订阅只执行一次（否则旧订阅残留在单例上，Closed 只能摘除最后一次）。
        Loaded -= MainWindow_Loaded;

        // 初始化数据库状态指示器
        UpdateDatabaseStatus();

        // 订阅故障切换服务的事件（保存引用以便后续取消订阅）
        var failoverService = DatabaseFailoverService.Instance;

        _propertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName is nameof(DatabaseFailoverService.CurrentRole)
                or nameof(DatabaseFailoverService.CurrentStatus)
                or nameof(DatabaseFailoverService.StatusMessage)
                or nameof(DatabaseFailoverService.CurrentServerDisplay)
                or nameof(DatabaseFailoverService.PrimaryConnectionStatus)
                or nameof(DatabaseFailoverService.SecondaryConnectionStatus))
            {
                // BeginInvoke 异步投递：故障切换事件由持 _lock 的定时器线程触发，
                // 同步 Invoke 会与等锁的 UI 线程互等死锁
                Dispatcher.BeginInvoke(new Action(UpdateDatabaseStatus));
            }
        };
        _roleChangedHandler = _ => Dispatcher.BeginInvoke(new Action(UpdateDatabaseStatus));
        _statusChangedHandler = _ => Dispatcher.BeginInvoke(new Action(UpdateDatabaseStatus));

        failoverService.PropertyChanged += _propertyChangedHandler;
        failoverService.RoleChanged += _roleChangedHandler;
        failoverService.StatusChanged += _statusChangedHandler;
    }

    /// <summary>
    /// 窗口关闭时释放资源
    /// </summary>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sessionTimer.Stop();

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
    /// 更新数据库状态指示器（导航栏显示主库/从库各自的连接状态）
    /// </summary>
    private void UpdateDatabaseStatus()
    {
        try
        {
            var service = DatabaseFailoverService.Instance;
            bool isOnPrimary = service.CurrentRole == DatabaseFailoverService.DatabaseRole.Primary;

            // === 主库行 ===
            var (primaryText, primaryColor) = service.PrimaryConnectionStatus switch
            {
                DatabaseFailoverService.DbConnectionStatus.Connected =>
                    ("正常", Color.FromRgb(0x16, 0xA3, 0x4A)),       // 绿色
                DatabaseFailoverService.DbConnectionStatus.Disconnected =>
                    ("连接失败", Color.FromRgb(0xDC, 0x26, 0x26)),    // 红色
                DatabaseFailoverService.DbConnectionStatus.NotConfigured =>
                    ("未配置", Color.FromRgb(0x64, 0x74, 0x8B)),      // 灰色
                _ => ("未知", Color.FromRgb(0x64, 0x74, 0x8B))
            };
            DbPrimaryDot.Fill = new SolidColorBrush(primaryColor);
            DbPrimaryStatusText.Text = primaryText;
            DbPrimaryStatusText.Foreground = new SolidColorBrush(primaryColor);
            DbPrimaryServerText.Text = service.PrimaryServerDisplay;
            DbPrimaryCurrentTag.Visibility = isOnPrimary ? Visibility.Visible : Visibility.Collapsed;

            // === 从库行 ===
            var (secondaryText, secondaryColor) = service.SecondaryConnectionStatus switch
            {
                DatabaseFailoverService.DbConnectionStatus.Connected =>
                    ("正常", Color.FromRgb(0x16, 0xA3, 0x4A)),       // 绿色
                DatabaseFailoverService.DbConnectionStatus.Disconnected =>
                    ("连接失败", Color.FromRgb(0xDC, 0x26, 0x26)),    // 红色
                DatabaseFailoverService.DbConnectionStatus.NotConfigured =>
                    ("未配置", Color.FromRgb(0x64, 0x74, 0x8B)),      // 灰色
                _ => ("未知", Color.FromRgb(0x64, 0x74, 0x8B))
            };
            DbSecondaryDot.Fill = new SolidColorBrush(secondaryColor);
            DbSecondaryStatusText.Text = secondaryText;
            DbSecondaryStatusText.Foreground = new SolidColorBrush(secondaryColor);
            DbSecondaryServerText.Text = service.SecondaryServerDisplay;
            DbSecondaryCurrentTag.Visibility = !isOnPrimary ? Visibility.Visible : Visibility.Collapsed;
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
