using System.Media;
using System.Windows;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 关键事件告警服务
/// 用于在主从切换、备份失败、主从都挂等场景通知操作员。
/// 线程安全：所有方法通过 Dispatcher 切换到 UI 线程显示弹窗。
/// </summary>
public static class AlertService
{
    private static readonly object _showLock = new();
    private static DateTime _lastAlertTime = DateTime.MinValue;
    private static readonly TimeSpan MinAlertInterval = TimeSpan.FromSeconds(30); // 防止弹窗风暴

    /// <summary>
    /// 显示关键告警（弹窗 + 声音）
    /// 自动节流：30 秒内只显示一次，防止弹窗风暴。
    /// </summary>
    /// <param name="title">弹窗标题</param>
    /// <param name="message">弹窗内容</param>
    /// <param name="forceShow">强制显示（忽略节流）</param>
    public static void ShowCriticalAlert(string title, string message, bool forceShow = false)
    {
        // 节流：防止短时间内大量弹窗
        if (!forceShow)
        {
            lock (_showLock)
            {
                if (DateTime.Now - _lastAlertTime < MinAlertInterval)
                {
                    Log.Debug("告警被节流跳过: {Title}", title);
                    return;
                }
                _lastAlertTime = DateTime.Now;
            }
        }

        Log.Warning("[告警] {Title}: {Message}", title, message);

        // 切换到 UI 线程显示弹窗
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(() => ShowAlertCore(title, message));
        }
        else
        {
            ShowAlertCore(title, message);
        }
    }

    private static void ShowAlertCore(string title, string message)
    {
        try
        {
            // 播放警告声音
            SystemSounds.Exclamation.Play();

            // 显示弹窗
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "显示告警弹窗失败");
        }
    }

    /// <summary>
    /// 数据库故障切换告警
    /// </summary>
    public static void AlertFailover(string fromServer, string toServer)
    {
        ShowCriticalAlert(
            "⚠️ 数据库故障切换",
            $"主库 ({fromServer}) 不可用，已自动切换到从库 ({toServer})。\n\n" +
            "请检查主库状态，系统会在主库恢复后自动切回。");
    }

    /// <summary>
    /// 数据库切回告警
    /// </summary>
    public static void AlertFailback(string server)
    {
        ShowCriticalAlert(
            "✅ 数据库已切回主库",
            $"主库 ({server}) 已恢复，系统已自动切回。\n\n" +
            "数据缓冲已自动补写。");
    }

    /// <summary>
    /// 主从都挂告警（严重）
    /// </summary>
    public static void AlertBothDatabasesDown()
    {
        ShowCriticalAlert(
            "🔴 严重：主库和从库均不可用",
            "主库和从库均无法连接！\n\n" +
            "• 数据正在缓冲到内存（上限 200MB）\n" +
            "• 请紧急检查数据库服务器状态\n" +
            "• 任一数据库恢复后系统将自动重连",
            forceShow: true); // 强制显示，这是严重事件
    }

    /// <summary>
    /// 备份连续失败告警
    /// </summary>
    public static void AlertBackupFailed(int failureCount, string errorMessage)
    {
        ShowCriticalAlert(
            "⚠️ 数据库备份失败",
            $"数据库备份已连续失败 {failureCount} 次。\n\n" +
            $"错误信息: {errorMessage}\n\n" +
            "• 系统将每 5 分钟自动重试\n" +
            "• 请检查磁盘空间和数据库状态");
    }

    /// <summary>
    /// 缓冲区即将满告警
    /// </summary>
    public static void AlertBufferNearlyFull(double memoryMB, double maxMemoryMB)
    {
        ShowCriticalAlert(
            "⚠️ 数据缓冲区即将满",
            $"数据缓冲区已使用 {memoryMB:F0}MB / {maxMemoryMB:F0}MB。\n\n" +
            "• 数据库可能长时间不可用\n" +
            "• 缓冲区满后旧数据将被丢弃\n" +
            "• 请尽快恢复数据库连接");
    }
}
