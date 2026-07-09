using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Serilog;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Views.Auth;

namespace IsolationLeakage.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private AppDbContext? _dbContext;

    private bool _handlingUnhandledException = false;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // ==================== 全局异常捕获（最优先） ====================
        DispatcherUnhandledException += (sender, args) =>
        {
            // 防止 MessageBox 渲染时再次触发异常导致无限递归（StackOverflow）
            if (_handlingUnhandledException) return;
            _handlingUnhandledException = true;

            try
            {
                Log.Error(args.Exception, "UI 线程未处理异常");
            }
            catch
            {
                // 日志系统也可能失败，静默忽略
            }

            try
            {
                MessageBox.Show(
                    $"发生未处理的错误：\n\n{args.Exception.Message}\n\n详细信息已记录到日志文件。",
                    "系统错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // MessageBox 渲染也可能失败（如 XAML 崩溃），此时无法弹出对话框
            }
            finally
            {
                args.Handled = true;
                _handlingUnhandledException = false;
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "非 UI 线程未处理异常 (IsTerminating={IsTerminating})", args.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        // ==================== 渲染优化（解决文本模糊问题） ====================
        // 1. 启用 GPU 硬件加速（禁用 SoftwareOnly 纯软件渲染）
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

        // 2. 全局默认 Display 渲染模式（比 Ideal 清晰，小字号下尤其明显）
        // 所有继承 TextElement 的控件（TextBlock/Label/Run/DataGridCell 等）自动生效
        TextOptions.TextFormattingModeProperty.OverrideMetadata(
            typeof(TextElement),
            new FrameworkPropertyMetadata(TextFormattingMode.Display, FrameworkPropertyMetadataOptions.Inherits));

        // ==================== 初始化 Serilog 日志 ====================
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "app-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 10_485_760,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("日志文件路径：{LogPath}", logPath);

        try
        {
            Log.Information("========== 应用程序启动 ==========");

            base.OnStartup(e);

            // 初始化数据库
            _dbContext = DbContextFactory.CreateDbContext();
            Log.Information("数据库上下文已创建，连接串: {ConnectionString}", DbContextFactory.GetDefaultConnectionString());

            // ── 启动依赖检查：SQL Server 连接预检 ──
            var checkResult = await CheckSqlServerAsync();
            if (!checkResult.IsSuccess)
            {
                Log.Warning("SQL Server 依赖检查失败: {Error}", checkResult.ErrorMessage);

                // 弹出配置对话框，让客户选择/输入实例名
                var currentServer = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
                    DbContextFactory.GetDefaultConnectionString()).DataSource;
                var configDialog = new Views.SqlServerConfigDialog(currentServer);
                var dialogResult = configDialog.ShowDialog();

                if (dialogResult != true)
                {
                    Log.Fatal("用户取消数据库配置，应用程序退出");
                    Log.CloseAndFlush();
                    Shutdown();
                    return;
                }

                // 用户配置成功，重新创建 DbContext 并重试连接
                Log.Information("用户已重新配置数据库连接，重试中...");
                _dbContext.Dispose();
                _dbContext = DbContextFactory.CreateDbContext();

                var retryResult = await CheckSqlServerAsync();
                if (!retryResult.IsSuccess)
                {
                    Log.Fatal("重新配置后连接仍然失败: {Error}", retryResult.ErrorMessage);
                    MessageBox.Show(
                        $"配置后仍然无法连接数据库：\n\n{retryResult.ErrorMessage}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    Log.CloseAndFlush();
                    Shutdown();
                    return;
                }
            }

            await DatabaseInitializer.InitializeAsync(_dbContext);
            Log.Information("数据库初始化完成");

            // 初始化服务定位器
            AppServices.Initialize(_dbContext);
            Log.Information("应用服务初始化完成");

            // 初始化自动备份服务（登录成功后启动）
            AutoBackupService.Instance.Initialize();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "数据库初始化失败，应用程序无法启动");

            MessageBox.Show(
                $"数据库初始化失败：{ex.Message}\n\n请确保 SQL Server 服务已启动且数据库可连接（连接串详见 appsettings.json）。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Log.CloseAndFlush();
            Shutdown();
            return;
        }

        // 设置显式关闭模式，防止 LoginWindow 关闭后 WPF 自动退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ── 显示登录窗口 ──
        var loginWindow = new Views.Auth.LoginWindow { Owner = null };
        MainWindow = loginWindow;
        loginWindow.ShowDialog();
        if (!Services.Security.UserSession.IsLoggedIn)
        {
            Log.Information("用户取消登录");
            Shutdown();
            return;
        }

        // 登录成功，显示主窗口
        Log.Information("登录成功，创建主窗口");
        try
        {
            MainWindow = null;  // 先解除对 LoginWindow 的引用
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Closed += (_, _) => Shutdown();  // 主窗口关闭时退出应用
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "主窗口初始化失败");
            MessageBox.Show($"主窗口初始化失败：{ex.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("========== 应用程序退出 ==========");

        // 释放通讯资源并断开所有设备连接
        AppServices.Shutdown();

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    #region 启动依赖检查

    private sealed record SqlCheckResult(bool IsSuccess, string? ErrorMessage)
    {
        public static SqlCheckResult Ok() => new(true, null);
        public static SqlCheckResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// 启动前预检 SQL Server 连接，失败时给出友好的中文提示。
    /// 先连接 master 数据库测试 SQL Server 服务是否正常（避免目标库不存在时误报）。
    /// </summary>
    private async Task<SqlCheckResult> CheckSqlServerAsync()
    {
        var connectionString = DbContextFactory.GetDefaultConnectionString();
        var builder = new SqlConnectionStringBuilder(connectionString);
        var serverName = builder.DataSource;
        var databaseName = builder.InitialCatalog;

        Log.Information("正在检查 SQL Server 连接，服务器: {Server}, 目标库: {Database}", serverName, databaseName);

        // 第一步：连接 master 测试 SQL Server 服务是否正常
        var masterBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        try
        {
            using var connection = new SqlConnection(masterBuilder.ToString());
            connection.Open();
            Log.Information("SQL Server 连接成功（master 数据库）");

            // 第二步：检查目标数据库是否存在，不存在则提示将自动创建
            var checkDbCmd = new SqlCommand(
                $"SELECT COUNT(*) FROM sys.databases WHERE name = '{databaseName}'",
                connection);
            var dbExists = (int)await checkDbCmd.ExecuteScalarAsync() > 0;

            if (!dbExists)
            {
                Log.Information("目标数据库 {Database} 不存在，将自动创建", databaseName);
            }

            return SqlCheckResult.Ok();
        }
        catch (SqlException ex)
        {
            // 根据错误号分类给出提示
            // 见 https://docs.microsoft.com/sql/relational-databases/errors-events/database-engine-events-and-errors
            var message = ex.Number switch
            {
                // 2 — 连接被拒绝 / 服务未启动 / 实例不存在
                2 =>
                    $"无法连接到 SQL Server 实例「{serverName}」。\n\n" +
                    $"可能原因：\n" +
                    $"  1. 本机未安装 SQL Server\n" +
                    $"  2. SQL Server 服务（{serverName}）未启动\n\n" +
                    $"请按以下步骤操作：\n" +
                    $"  • 安装 SQL Server Express（免费）\n" +
                    $"    https://www.microsoft.com/sql-server/sql-server-downloads\n" +
                    $"  • 安装时创建名为「{ExtractInstanceName(serverName)}」的命名实例\n" +
                    $"  • 安装完成后在「服务」中确认 SQL Server ({ExtractInstanceName(serverName)}) 已启动\n\n" +
                    $"详细信息：{ex.Message}",

                // 53 / 40 — 网络不可达 / 服务器不存在
                53 or 40 =>
                    $"找不到 SQL Server 实例「{serverName}」。\n\n" +
                    $"请确认：\n" +
                    $"  1. 已安装 SQL Server，且实例名为「{ExtractInstanceName(serverName)}」\n" +
                    $"  2. SQL Server 服务正在运行（可在「服务」中查看）\n" +
                    $"  3. 已启用 TCP/IP 和 Named Pipes 协议\n\n" +
                    $"详细信息：{ex.Message}",

                // 18456 — 登录失败
                18456 =>
                    $"SQL Server 登录失败。\n\n" +
                    $"当前使用 Windows 身份验证连接「{serverName}」。\n" +
                    $"请确认当前 Windows 用户有访问该 SQL Server 实例的权限。\n\n" +
                    $"详细信息：{ex.Message}",

                // 其他错误
                _ =>
                    $"连接 SQL Server「{serverName}」失败（错误号: {ex.Number}）。\n\n" +
                    $"请确保 SQL Server 已安装且服务正在运行。\n\n" +
                    $"详细信息：{ex.Message}",
            };

            Log.Error(ex, "SQL Server 连接失败，错误号: {ErrorNumber}", ex.Number);
            return SqlCheckResult.Fail(message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SQL Server 连接检查发生未知异常");
            return SqlCheckResult.Fail($"连接数据库时发生未知错误：\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// 从 DataSource 中提取实例名。
    /// 例如 ".\CITADEL" → "CITADEL"，"(localdb)\MSSQLLocalDB" → "MSSQLLocalDB"，"localhost" → "MSSQLSERVER"
    /// </summary>
    private static string ExtractInstanceName(string dataSource)
    {
        var backslashIndex = dataSource.LastIndexOf('\\');
        if (backslashIndex >= 0 && backslashIndex < dataSource.Length - 1)
            return dataSource[(backslashIndex + 1)..];

        // 无实例名 → 默认实例
        return "MSSQLSERVER";
    }

    #endregion
}
