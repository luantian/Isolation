using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class SystemManagementView : UserControl
{
    // 保存事件处理器引用，以便在 Unloaded 时反注册（DatabaseFailoverService 是应用级单例，
    // 若不退订会强引用每一个历史 View 实例，造成随运行时长无界增长的内存泄漏）。
    private PropertyChangedEventHandler? _failoverHandler;

    public SystemManagementView()
    {
        InitializeComponent();
        Loaded += SystemManagementView_Loaded;
        Unloaded += SystemManagementView_Unloaded;
    }

    private void SystemManagementView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateFailoverStatus();

        // 订阅故障切换服务的事件（防重复订阅：Loaded 可能随可视树重新挂载多次触发）
        if (_failoverHandler != null) return;

        _failoverHandler = (_, args) =>
        {
            if (args.PropertyName is nameof(DatabaseFailoverService.CurrentRole)
                or nameof(DatabaseFailoverService.CurrentStatus)
                or nameof(DatabaseFailoverService.IsEnabled)
                or nameof(DatabaseFailoverService.StatusMessage)
                or nameof(DatabaseFailoverService.CurrentServerDisplay))
            {
                // BeginInvoke 异步投递：故障切换事件由持 _lock 的定时器线程触发，
                // 同步 Invoke 会与等锁的 UI 线程互等死锁
                Dispatcher.BeginInvoke(new Action(UpdateFailoverStatus));
            }
        };
        DatabaseFailoverService.Instance.PropertyChanged += _failoverHandler;
    }

    private void SystemManagementView_Unloaded(object sender, RoutedEventArgs e)
    {
        // 反注册，解除单例对本 View 实例的强引用
        if (_failoverHandler != null)
        {
            DatabaseFailoverService.Instance.PropertyChanged -= _failoverHandler;
            _failoverHandler = null;
        }
    }

    private void UpdateFailoverStatus()
    {
        try
        {
            var service = DatabaseFailoverService.Instance;

            // 角色
            FailoverRoleText.Text = service.CurrentRole == DatabaseFailoverService.DatabaseRole.Primary
                ? "主库" : "从库";

            // 状态
            var (statusText, statusColor) = service.CurrentStatus switch
            {
                DatabaseFailoverService.DatabaseStatus.Normal =>
                    ("正常", Color.FromRgb(0x16, 0xA3, 0x4A)),
                DatabaseFailoverService.DatabaseStatus.Checking =>
                    ("检测中", Color.FromRgb(0x25, 0x63, 0xEB)),
                DatabaseFailoverService.DatabaseStatus.FailingOver =>
                    ("切换中", Color.FromRgb(0xD9, 0x77, 0x06)),
                DatabaseFailoverService.DatabaseStatus.OnSecondary =>
                    ("从库运行", Color.FromRgb(0xD9, 0x77, 0x06)),
                DatabaseFailoverService.DatabaseStatus.WaitingFailback =>
                    ("等待切回", Color.FromRgb(0x25, 0x63, 0xEB)),
                DatabaseFailoverService.DatabaseStatus.Disabled =>
                    ("未启用", Color.FromRgb(0x64, 0x74, 0x8B)),
                _ => ("未知", Color.FromRgb(0x64, 0x74, 0x8B))
            };
            FailoverStatusText.Text = statusText;
            FailoverStatusText.Foreground = new SolidColorBrush(statusColor);

            // 启用状态
            FailoverEnabledText.Text = service.IsEnabled ? "已启用" : "未启用";
            FailoverEnabledText.Foreground = service.IsEnabled
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

            // 服务器
            FailoverServerText.Text = $"服务器：{service.CurrentServerDisplay}";
        }
        catch
        {
            // UI 更新不应导致崩溃
        }
    }

    private void OperationLogGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is SystemManagementViewModel vm)
        {
            vm.OperationLogPage.ViewDetailCommand.Execute(null);
        }
    }

    private void FailoverConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FailoverConfigDialog();
        dialog.ShowDialog();
    }

    private void DatabaseConfig_Click(object sender, RoutedEventArgs e)
    {
        var currentServer = Data.DbContextFactory.GetDefaultConnectionString();
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(currentServer);
            currentServer = builder.DataSource;
        }
        catch { }

        var dialog = new SqlServerConfigDialog(currentServer);
        dialog.ShowDialog();
    }
}
