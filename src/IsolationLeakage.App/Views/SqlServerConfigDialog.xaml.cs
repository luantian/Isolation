using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using Serilog;

namespace IsolationLeakage.App.Views;

/// <summary>
/// SQL Server 实例配置对话框 — 启动时连接失败时弹出，让客户输入实例名
/// </summary>
public partial class SqlServerConfigDialog : Window
{
    /// <summary>
    /// 测试成功后生成的完整连接字符串
    /// </summary>
    public string? ResultConnectionString { get; private set; }

    private bool _connectionTested;

    public SqlServerConfigDialog(string currentServer)
    {
        InitializeComponent();

        // 预填当前实例地址
        InstanceTextBox.Text = currentServer;
    }

    #region 事件处理

    private void InstanceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InstanceTextBox.Text.Trim();
        TestButton.IsEnabled = !string.IsNullOrEmpty(text);
        _connectionTested = false;
        TestResultText.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var server = InstanceTextBox.Text.Trim();
        if (string.IsNullOrEmpty(server)) return;

        // 构建连接串（保留原配置中的数据库名和认证方式）
        var connStr = BuildConnectionString(server);

        TestButton.IsEnabled = false;
        TestButton.Content = "测试中...";
        TestResultText.Visibility = Visibility.Visible;
        TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // muted
        TestResultText.Text = "正在连接...";

        try
        {
            using var connection = new SqlConnection(connStr);
            await Task.Run(() => connection.Open());

            _connectionTested = true;
            ResultConnectionString = connStr;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // green
            TestResultText.Text = $"✅ 连接成功！已连接到 {server}";
            ConfirmButton.IsEnabled = true;
        }
        catch (SqlException ex)
        {
            _connectionTested = false;
            ConfirmButton.IsEnabled = false;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)); // red
            TestResultText.Text = $"❌ {GetFriendlySqlError(ex)}";
        }
        catch (Exception ex)
        {
            _connectionTested = false;
            ConfirmButton.IsEnabled = false;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            TestResultText.Text = $"❌ 连接失败：{ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "测试连接";
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_connectionTested || ResultConnectionString == null)
        {
            MessageBox.Show("请先点击「测试连接」确认可以正常连接。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 保存到 appsettings.json
            AppConfiguration.SaveConnectionString("DefaultConnection", ResultConnectionString);

            // 更新运行时内存
            DbContextFactory.Configure(ResultConnectionString);

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

    /// <summary>
    /// 基于用户输入的实例地址构建完整连接串
    /// </summary>
    private static string BuildConnectionString(string server)
    {
        // 尝试基于当前连接串修改 Server 部分
        try
        {
            var current = DbContextFactory.GetDefaultConnectionString();
            var builder = new SqlConnectionStringBuilder(current)
            {
                DataSource = server
            };
            return builder.ConnectionString;
        }
        catch
        {
            // 回退到默认模板
            return $"Server={server};Database=IsolationLeakageDb;Trusted_Connection=True;TrustServerCertificate=True;";
        }
    }

    /// <summary>
    /// 将 SqlException 转为简短的中文友好提示
    /// </summary>
    private static string GetFriendlySqlError(SqlException ex)
    {
        return ex.Number switch
        {
            2 => "服务未启动或实例不存在，请确认 SQL Server 服务已运行",
            53 or 40 => "找不到 SQL Server 实例，请检查实例名是否正确",
            18456 => "登录失败，请检查当前 Windows 账户是否有访问权限",
            5 => "网络不可达，请检查实例名和网络配置",
            _ => $"连接失败（错误 {ex.Number}）",
        };
    }

    #endregion
}
