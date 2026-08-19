using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Services;
using Serilog;

namespace IsolationLeakage.App.Views;

/// <summary>
/// 数据库高可用配置对话框 — 配置主从切换参数
/// </summary>
public partial class FailoverConfigDialog : Window
{
    private bool _secondaryTested;
    private string? _secondaryConnectionString;

    public FailoverConfigDialog()
    {
        InitializeComponent();
        LoadCurrentConfig();
    }

    #region 加载配置

    private void LoadCurrentConfig()
    {
        var failoverService = DatabaseFailoverService.Instance;

        // 启用/禁用
        EnableFailoverCheckBox.IsChecked = failoverService.IsEnabled;

        // 从库连接
        var secondaryConn = AppConfiguration.GetConnectionString("SecondaryConnection");
        if (!string.IsNullOrWhiteSpace(secondaryConn))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(secondaryConn);
                SecondaryServerTextBox.Text = builder.DataSource;
                if (!builder.IntegratedSecurity)
                {
                    SecSqlAuthRadio.IsChecked = true;
                    SecSqlAuthPanel.Visibility = Visibility.Visible;
                    SecSqlUsernameTextBox.Text = builder.UserID;
                }
            }
            catch
            {
                SecondaryServerTextBox.Text = secondaryConn;
            }
        }

        // 高级参数
        var failoverSection = AppConfiguration.Instance.GetSection("Failover");
        HealthCheckIntervalTextBox.Text = (int.TryParse(failoverSection?.GetSection("HealthCheckIntervalSeconds")?.Value, out var hc) ? hc : 15).ToString();
        ConnectionTimeoutTextBox.Text = (int.TryParse(failoverSection?.GetSection("ConnectionTimeoutSeconds")?.Value, out var ct) ? ct : 5).ToString();
        FailbackDelayTextBox.Text = (int.TryParse(failoverSection?.GetSection("FailbackDelaySeconds")?.Value, out var fd) ? fd : 60).ToString();
        MaxRetryTextBox.Text = (int.TryParse(failoverSection?.GetSection("MaxRetryBeforeFailover")?.Value, out var mr) ? mr : 2).ToString();

        // 当前状态
        UpdateStatusDisplay();
    }

    private void UpdateStatusDisplay()
    {
        var failoverService = DatabaseFailoverService.Instance;

        CurrentRoleText.Text = failoverService.CurrentRole == DatabaseFailoverService.DatabaseRole.Primary
            ? "主库" : "从库";

        CurrentStatusText.Text = failoverService.CurrentStatus switch
        {
            DatabaseFailoverService.DatabaseStatus.Disabled => "未启用",
            DatabaseFailoverService.DatabaseStatus.Normal => "正常",
            DatabaseFailoverService.DatabaseStatus.Checking => "检测中",
            DatabaseFailoverService.DatabaseStatus.FailingOver => "切换中",
            DatabaseFailoverService.DatabaseStatus.OnSecondary => "从库运行",
            DatabaseFailoverService.DatabaseStatus.WaitingFailback => "等待切回",
            _ => "未知"
        };

        CurrentStatusText.Foreground = failoverService.CurrentStatus switch
        {
            DatabaseFailoverService.DatabaseStatus.Normal => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
            DatabaseFailoverService.DatabaseStatus.OnSecondary => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            DatabaseFailoverService.DatabaseStatus.FailingOver => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B))
        };

        CurrentServerText.Text = failoverService.CurrentServerDisplay;
    }

    #endregion

    #region 事件处理

    private void EnableFailover_Changed(object sender, RoutedEventArgs e)
    {
        var isEnabled = EnableFailoverCheckBox.IsChecked == true;
        SecondaryServerTextBox.IsEnabled = isEnabled;
        TestSecondaryButton.IsEnabled = isEnabled && !string.IsNullOrWhiteSpace(SecondaryServerTextBox.Text);
        SecWindowsAuthRadio.IsEnabled = isEnabled;
        SecSqlAuthRadio.IsEnabled = isEnabled;
        HealthCheckIntervalTextBox.IsEnabled = isEnabled;
        ConnectionTimeoutTextBox.IsEnabled = isEnabled;
        FailbackDelayTextBox.IsEnabled = isEnabled;
        MaxRetryTextBox.IsEnabled = isEnabled;

        if (!isEnabled)
        {
            SecSqlAuthPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 启用时根据当前选择的认证方式设置面板可见性
            SecSqlAuthPanel.Visibility = SecSqlAuthRadio.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SecAuthMode_Changed(object sender, RoutedEventArgs e)
    {
        // 切换认证方式时更新用户名/密码面板的可见性
        SecSqlAuthPanel.Visibility = SecSqlAuthRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 从库地址/用户名/密码任一输入变更后，使"已测试"标记与缓存的连接串失效：
    /// 否则测试成功后修改地址再保存，保存的仍是旧地址的连接串
    /// （用户以为新地址已生效，实际故障切换时连的是旧机器）。
    /// </summary>
    private void SecondaryInput_Changed(object sender, RoutedEventArgs e)
    {
        // 尚未测试过（含 LoadCurrentConfig 回填初始文本期间）无需失效
        if (!_secondaryTested && _secondaryConnectionString == null) return;

        _secondaryTested = false;
        _secondaryConnectionString = null;

        if (SecondaryTestResult == null) return;

        SecondaryTestResult.Visibility = Visibility.Visible;
        SecondaryTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
        SecondaryTestResult.Text = "从库配置已修改，请重新测试连接后再保存";
    }

    private void TestSecondary_Click(object sender, RoutedEventArgs e)
    {
        var server = SecondaryServerTextBox.Text.Trim();
        if (string.IsNullOrEmpty(server))
        {
            MessageBox.Show("请输入从库服务器地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 如果选择 SQL Server 身份验证，验证用户名和密码
        if (SecSqlAuthRadio.IsChecked == true)
        {
            var username = SecSqlUsernameTextBox.Text.Trim();
            var password = SecSqlPasswordBox.Password;

            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("请输入 SQL Server 用户名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                SecSqlUsernameTextBox.Focus();
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("请输入 SQL Server 密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                SecSqlPasswordBox.Focus();
                return;
            }
        }

        _secondaryConnectionString = BuildSecondaryConnectionString(server);

        TestSecondaryButton.IsEnabled = false;
        TestSecondaryButton.Content = "测试中...";
        SecondaryTestResult.Visibility = Visibility.Visible;
        SecondaryTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        SecondaryTestResult.Text = "正在连接...";

        Task.Run(() =>
        {
            try
            {
                using var connection = new SqlConnection(_secondaryConnectionString);
                connection.Open();
                _secondaryTested = true;

                Dispatcher.Invoke(() =>
                {
                    SecondaryTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
                    SecondaryTestResult.Text = $"✅ 从库连接成功！";
                });
            }
            catch (SqlException ex)
            {
                _secondaryTested = false;
                Dispatcher.Invoke(() =>
                {
                    SecondaryTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                    SecondaryTestResult.Text = $"❌ 从库连接失败\n错误代码：{ex.Number}\n{ex.Message}\n\n请检查从库服务器地址、用户名密码，以及 SQL Server 是否已启用 TCP/IP 和防火墙是否放行 1433 端口";
                });
            }
            catch (Exception ex)
            {
                _secondaryTested = false;
                Dispatcher.Invoke(() =>
                {
                    SecondaryTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                    SecondaryTestResult.Text = $"❌ 连接失败：{ex.InnerException?.Message ?? ex.Message}\n\n请检查从库服务器地址和网络连接";
                });
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    TestSecondaryButton.IsEnabled = true;
                    TestSecondaryButton.Content = "测试连接";
                });
            }
        });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var isEnabled = EnableFailoverCheckBox.IsChecked == true;

            if (isEnabled)
            {
                // 保存从库连接字符串
                if (!_secondaryTested || _secondaryConnectionString == null)
                {
                    MessageBox.Show("请先测试从库连接是否正常。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // 使用 AppConfiguration 统一保存，保持格式一致
                AppConfiguration.SaveConnectionString("SecondaryConnection", _secondaryConnectionString);
            }
            else
            {
                AppConfiguration.SaveConnectionString("SecondaryConnection", string.Empty);
            }

            // 保存故障切换配置
            var failoverSettings = new FailoverSettings
            {
                Enabled = isEnabled,
                HealthCheckIntervalSeconds = int.TryParse(HealthCheckIntervalTextBox.Text, out var hc) ? hc : 15,
                ConnectionTimeoutSeconds = int.TryParse(ConnectionTimeoutTextBox.Text, out var ct) ? ct : 5,
                FailbackDelaySeconds = int.TryParse(FailbackDelayTextBox.Text, out var fd) ? fd : 60,
                MaxRetryBeforeFailover = int.TryParse(MaxRetryTextBox.Text, out var mr) ? mr : 2,
            };
            AppConfiguration.SaveFailoverSettings(failoverSettings);

            // 通知故障切换服务重新加载
            DatabaseFailoverService.Instance.ReloadConfiguration();

            Log.Information("数据库高可用配置已保存，启用={Enabled}", isEnabled);
            MessageBox.Show("配置已保存。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存配置失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion

    #region 辅助方法

    private string BuildSecondaryConnectionString(string server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "IsolationLeakageDb",
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        };

        if (SecWindowsAuthRadio.IsChecked == true)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = SecSqlUsernameTextBox.Text.Trim();
            builder.Password = SecSqlPasswordBox.Password;
        }

        return builder.ConnectionString;
    }

    #endregion
}
