using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Serilog;
using IsolationLeakage.App.Configuration;
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
    private readonly object _dbContextLock = new();

    private bool _handlingUnhandledException = false;

    /// <summary>
    /// 数据库故障切换时重建 DbContext。
    /// 在 UI 线程上执行，避免与其他线程并发访问 DbContext。
    /// </summary>
    private void OnDatabaseConnectionChanged()
    {
        // 切换到 UI 线程执行，保证线程安全
        if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(OnDatabaseConnectionChanged);
            return;
        }

        var newConnectionString = DbContextFactory.GetActiveConnectionString();
        // 脱敏日志：不打印完整连接字符串（可能含密码）
        var serverDisplay = MaskConnectionString(newConnectionString);
        Serilog.Log.Information("数据库连接已切换，重建 DbContext: {Server}", serverDisplay);

        lock (_dbContextLock)
        {
            try
            {
                // 创建新的 DbContext
                var optionsBuilder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
                optionsBuilder.UseSqlServer(newConnectionString, sql => sql.UseCompatibilityLevel(100));
                var newDbContext = new AppDbContext(optionsBuilder.Options);

                // 验证新连接可用
                if (!newDbContext.Database.CanConnect())
                {
                    Serilog.Log.Error("切换后的数据库连接无法使用，回退到原连接");
                    newDbContext.Dispose();
                    return;
                }

                // 替换旧 DbContext
                var oldDbContext = _dbContext;
                _dbContext = newDbContext;

                // 重新初始化 AppServices（仅替换 DB 相关服务，保留 PLC 连接）
                AppServices.ReinitializeDataServices(_dbContext);

                // 释放旧 DbContext
                oldDbContext?.Dispose();

                Serilog.Log.Information("数据库连接切换完成，数据服务已重新初始化");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "数据库连接切换时发生异常");
            }
        }
    }

    /// <summary>
    /// 连接字符串脱敏（隐藏密码）
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return $"{builder.DataSource}/{builder.InitialCatalog}";
        }
        catch
        {
            return "(无法解析)";
        }
    }

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

            // ── 初始化数据库故障切换服务（先初始化，以便启动时使用） ──
            var failoverService = DatabaseFailoverService.Instance;
            failoverService.Initialize();
            failoverService.DbConnectionChanged += OnDatabaseConnectionChanged;

            // 初始化数据库
            _dbContext = DbContextFactory.CreateDbContext();
            Log.Information("数据库上下文已创建，连接串: {ConnectionString}", DbContextFactory.GetDefaultConnectionString());

            // ── 启动依赖检查：SQL Server 连接预检 ──
            var checkResult = await CheckSqlServerAsync();
            if (!checkResult.IsSuccess)
            {
                Log.Warning("主库 SQL Server 连接检查失败: {Error}", checkResult.ErrorMessage);

                // 如果故障切换已启用且从库可用，自动切到从库
                bool switchedToSecondary = false;
                if (failoverService.IsEnabled)
                {
                    var secondaryConn = AppConfiguration.GetConnectionString("SecondaryConnection");
                    if (!string.IsNullOrWhiteSpace(secondaryConn) && failoverService.TestConnectionString(secondaryConn))
                    {
                        Log.Warning("主库不可用，自动切换到从库启动");
                        _dbContext.Dispose();

                        // 用从库连接创建 DbContext
                        var optionsBuilder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
                        optionsBuilder.UseSqlServer(secondaryConn, sql => sql.UseCompatibilityLevel(100));
                        _dbContext = new AppDbContext(optionsBuilder.Options);

                        // 通知故障切换服务强制切到从库
                        failoverService.ForceSwitchTo(DatabaseFailoverService.DatabaseRole.Secondary);
                        switchedToSecondary = true;

                        Log.Information("已从从库启动");
                    }
                    else
                    {
                        Log.Warning("从库也无法连接，故障切换不可用");
                    }
                }

                // 如果未能切到从库，弹出配置对话框
                if (!switchedToSecondary)
                {
                    var currentServer = new SqlConnectionStringBuilder(
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
                    failoverService.ReloadConfiguration();
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
            }

            // 从库启动时跳过迁移（从库是备份还原的，不需要迁移；迁移可能导致从库数据不一致）
            bool isStartingFromSecondary = failoverService.CurrentRole == DatabaseFailoverService.DatabaseRole.Secondary;
            if (isStartingFromSecondary)
            {
                Log.Warning("从库启动，跳过数据库迁移（避免在从库上执行迁移）");
            }
            else
            {
                // 先确保数据库存在（如果不存在则创建）
                try
                {
                    var dbName = new SqlConnectionStringBuilder(DbContextFactory.GetDefaultConnectionString()).InitialCatalog;
                    Log.Information("确保数据库 {Database} 存在", dbName);
                    await CreateDatabaseIfNotExistsAsync(_dbContext, dbName);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "创建数据库失败，将继续尝试迁移");
                }

                await DatabaseInitializer.InitializeAsync(_dbContext);
                Log.Information("数据库初始化完成");
            }

            // 初始化服务定位器
            AppServices.Initialize(_dbContext);
            Log.Information("应用服务初始化完成");

            // 初始化自动备份服务（登录成功后启动）
            await AutoBackupService.Instance.InitializeAsync();

            // 启动数据库故障切换健康检查
            failoverService.Start();
            Log.Information("数据库故障切换服务已启动");
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

        // 停止数据库故障切换服务
        DatabaseFailoverService.Instance.Dispose();

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

    /// <summary>
    /// 如果目标数据库不存在，则创建它。
    /// 使用 master 数据库执行 CREATE DATABASE 语句。
    /// </summary>
    private static async Task CreateDatabaseIfNotExistsAsync(AppDbContext context, string databaseName)
    {
        var connectionString = DbContextFactory.GetDefaultConnectionString();
        var builder = new SqlConnectionStringBuilder(connectionString);
        var serverName = builder.DataSource;

        // 切换到 master 数据库执行 CREATE DATABASE
        var masterBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        using var connection = new SqlConnection(masterBuilder.ToString());
        await connection.OpenAsync();

        // 检查数据库是否存在
        var checkCmd = new SqlCommand($"SELECT COUNT(*) FROM sys.databases WHERE name = '{databaseName}'", connection);
        var exists = (int)await checkCmd.ExecuteScalarAsync() > 0;

        if (!exists)
        {
            Log.Information("数据库 {Database} 不存在，正在创建...", databaseName);
            var createCmd = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection);
            await createCmd.ExecuteNonQueryAsync();
            Log.Information("数据库 {Database} 创建成功", databaseName);
        }
        else
        {
            Log.Information("数据库 {Database} 已存在", databaseName);
        }
    }

    #endregion
}
