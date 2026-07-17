using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using Serilog;

namespace IsolationLeakage.App.Views;

/// <summary>
/// SQL Server 实例配置对话框 — 启动时连接失败时弹出，让客户配置数据库连接。
/// 支持本地实例和远程 IP 地址（WiFi/局域网），支持 Windows/SQL Server 身份验证。
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

        // 根据当前连接串检测认证方式
        try
        {
            var current = DbContextFactory.GetDefaultConnectionString();
            var builder = new SqlConnectionStringBuilder(current);
            if (!builder.IntegratedSecurity)
            {
                SqlAuthRadio.IsChecked = true;
                SqlAuthPanel.Visibility = Visibility.Visible;
                SqlUsernameTextBox.Text = builder.UserID;
            }
        }
        catch
        {
            // 默认 Windows 认证
        }
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

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlAuthPanel == null) return; // 设计时保护
        SqlAuthPanel.Visibility = SqlAuthRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        _connectionTested = false;
        TestResultText.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
    }

    private void SqlPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _connectionTested = false;
        TestResultText.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var server = InstanceTextBox.Text.Trim();
        if (string.IsNullOrEmpty(server)) return;

        // 构建连接串
        var connStr = BuildConnectionString(server);

        // 测试连接时使用 master 数据库（避免目标库不存在时误报）
        var testConnStr = BuildTestConnectionString(server);

        TestButton.IsEnabled = false;
        TestButton.Content = "测试中...";
        TestResultText.Visibility = Visibility.Visible;
        TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // muted
        TestResultText.Text = "正在连接...";

        try
        {
            using var connection = new SqlConnection(testConnStr);
            await Task.Run(() => connection.Open());

            _connectionTested = true;
            ResultConnectionString = connStr;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // green

            // 显示连接类型提示
            var isRemote = IsRemoteServer(server);
            var connType = isRemote ? "（远程连接）" : "（本地连接）";
            TestResultText.Text = $"✅ 连接成功！已连接到 {server} {connType}";
            ConfirmButton.IsEnabled = true;
        }
        catch (SqlException ex)
        {
            _connectionTested = false;
            ConfirmButton.IsEnabled = false;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)); // red
            TestResultText.Text = $"❌ {GetFriendlyError(ex, server)}";
        }
        catch (Exception ex)
        {
            _connectionTested = false;
            ConfirmButton.IsEnabled = false;

            TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            TestResultText.Text = $"❌ {GetFriendlyError(ex, server)}";
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

            // 通知故障切换服务重新加载配置
            Services.DatabaseFailoverService.Instance.ReloadConfiguration();

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
    /// 基于用户输入和认证方式构建完整连接串（用于保存配置）
    /// </summary>
    private string BuildConnectionString(string server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "IsolationLeakageDb",
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        };

        if (WindowsAuthRadio.IsChecked == true)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = SqlUsernameTextBox.Text.Trim();
            builder.Password = SqlPasswordBox.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// 构建测试用连接串（连接 master 数据库，避免目标库不存在时误报）
    /// </summary>
    private string BuildTestConnectionString(string server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        };

        if (WindowsAuthRadio.IsChecked == true)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = SqlUsernameTextBox.Text.Trim();
            builder.Password = SqlPasswordBox.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// 判断服务器地址是否为远程 IP（非本地实例）
    /// </summary>
    private static bool IsRemoteServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        if (server == "." || server.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (server.StartsWith(".\\") || server.StartsWith("(local)"))
            return false;
        // 包含 IP 地址格式（数字.数字.数字.数字）
        if (System.Net.IPAddress.TryParse(server.Split(',')[0].Split('\\')[0], out _))
            return true;
        return false;
    }

    /// <summary>
    /// 将异常转为详细的中文友好提示（含排查步骤）
    /// </summary>
    private static string GetFriendlyError(Exception ex, string server)
    {
        // 分析错误，给出具体原因和排查步骤
        if (ex is SqlException sqlEx)
        {
            return GetFriendlySqlError(sqlEx, server);
        }

        // 非 SqlException 的通用错误
        var innerMsg = ex.InnerException?.Message ?? ex.Message;
        return $"连接失败\n\n{innerMsg}\n\n" +
               $"请检查：\n" +
               $"  • 服务器地址是否正确：{server}\n" +
               $"  • SQL Server 服务是否已启动\n" +
               $"  • 防火墙是否已放行 1433 端口";
    }

    /// <summary>
    /// 将 SqlException 转为详细的中文友好提示
    /// </summary>
    private static string GetFriendlySqlError(SqlException ex, string server)
    {
        var isRemote = !string.IsNullOrWhiteSpace(server) &&
                       (server.Contains(".") && !server.StartsWith(".\\")) ||
                       server.Contains(",");

        // 构建排查建议（远程连接和本地连接的建议不同）
        var remoteTips = isRemote
            ? "\n\n远程连接排查步骤：\n" +
              "  1. 确认服务器 IP 地址正确（在服务器上用 ipconfig 查看）\n" +
              "  2. 确认本机可以 ping 通服务器：打开 cmd 输入 ping " + (server.Split('\\')[0].Split(',')[0]) + "\n" +
              "  3. 确认 SQL Server 已启用 TCP/IP 协议\n" +
              "     （SQL Server 配置管理器 → SQL Server 网络配置 → 启用 TCP/IP）\n" +
              "  4. 确认服务器防火墙已放行 TCP 1433 端口\n" +
              "  5. 确认 SQL Server Browser 服务已启动（命名实例需要）"
            : "";

        return ex.Number switch
        {
            // 网络不可达 / 实例不存在
            2 =>
                "无法连接到 SQL Server\n\n" +
                $"服务器地址：{server}\n\n" +
                "可能原因：\n" +
                "  • SQL Server 服务未启动\n" +
                "  • 实例名不正确\n" +
                "  • 本服务未安装 SQL Server\n\n" +
                "请确认：\n" +
                "  • 打开「服务」确认 SQL Server 服务正在运行\n" +
                "  • 实例名拼写正确（如 .\\SQLEXPRESS 或 192.168.1.100\\SQLEXPRESS）" +
                remoteTips,

            // 找不到服务器 / 网络名称无效
            53 or 40 =>
                "找不到 SQL Server 实例\n\n" +
                $"服务器地址：{server}\n\n" +
                "请检查：\n" +
                "  • 服务器地址是否填写正确\n" +
                "  • 如果是远程 IP，请确认网络通畅（可 ping 通）\n" +
                "  • SQL Server 已启用 TCP/IP 协议" +
                remoteTips,

            // 登录失败
            18456 =>
                "登录失败\n\n" +
                "请检查：\n" +
                "  • 用户名是否正确\n" +
                "  • 密码是否正确\n" +
                "  • 如果是远程连接，请确认 SQL Server 已启用混合认证模式\n" +
                "    （服务器属性 → 安全性 → SQL Server 和 Windows 身份验证模式）",

            // 访问被拒绝
            5 =>
                "网络访问被拒绝\n\n" +
                $"服务器地址：{server}\n\n" +
                "请检查：\n" +
                "  • IP 地址是否正确\n" +
                "  • 服务器防火墙是否已放行 TCP 1433 端口\n" +
                "  • 网络策略是否允许访问该端口" +
                remoteTips,

            // 连接超时
            10060 =>
                "连接超时\n\n" +
                $"服务器地址：{server}\n\n" +
                "服务器在 15 秒内没有响应。可能原因：\n" +
                "  • 网络不通（远程连接请检查 WiFi/局域网是否正常）\n" +
                "  • 防火墙阻止了连接\n" +
                "  • SQL Server 服务负载过高" +
                remoteTips,

            // 传输级错误 / 网络层失败（-1 通常是这个）
            -1 or -2 =>
                "网络连接失败\n\n" +
                $"服务器地址：{server}\n" +
                $"详细错误：{ex.Message}\n\n" +
                "最常见的原因：\n" +
                "  • 服务器 IP 不正确或服务器已关机\n" +
                "  • 防火墙阻止了数据库端口（1433）\n" +
                "  • SQL Server 未启用 TCP/IP 协议\n" +
                "  • 网络不稳定（WiFi 信号弱）" +
                remoteTips,

            // 连接被拒绝
            10061 =>
                "连接被拒绝\n\n" +
                $"服务器地址：{server}\n\n" +
                "服务器明确拒绝了连接请求。请确认：\n" +
                "  • SQL Server 服务已启动\n" +
                "  • TCP/IP 协议已启用，且端口为 1433\n" +
                "  • 端口没有被其他程序占用" +
                remoteTips,

            // 其他错误：显示详细信息 + 排查建议
            _ =>
                $"连接失败（错误代码：{ex.Number}）\n\n" +
                $"详细信息：{ex.Message}\n\n" +
                "通用排查步骤：\n" +
                "  • 确认 SQL Server 服务正在运行\n" +
                "  • 确认服务器地址正确\n" +
                "  • 确认防火墙已放行 1433 端口\n" +
                "  • 确认 SQL Server 已启用 TCP/IP 协议" +
                remoteTips,
        };
    }

    #endregion
}
