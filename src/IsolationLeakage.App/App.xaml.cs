using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
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
            await DatabaseInitializer.InitializeAsync(_dbContext);
            Log.Information("数据库初始化完成");

            // 初始化服务定位器
            AppServices.Initialize(_dbContext);
            Log.Information("应用服务初始化完成");
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

        // ── 自动登录 admin 账户（用于权限测试） ──
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var authService = new Services.Security.AuthService(context);
            var result = await authService.LoginAsync("admin", "admin123");
            if (result.IsSuccess)
            {
                var roles = await authService.LoadRolesAsync(result.User!.UserId);
                Services.Security.UserSession.Initialize(result.User, roles, result.Permissions);
                Log.Information("自动登录成功：admin（超级管理员）");
            }
            else
            {
                Log.Warning("自动登录失败：{Error}", result.Error);
                // 回退到手动登录
                var loginWindow = new Views.Auth.LoginWindow { Owner = null };
                MainWindow = loginWindow;
                loginWindow.ShowDialog();
                if (!Services.Security.UserSession.IsLoggedIn)
                {
                    Log.Information("用户取消登录");
                    Shutdown();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动登录异常，回退到手动登录");
            var loginWindow = new Views.Auth.LoginWindow { Owner = null };
            MainWindow = loginWindow;
            loginWindow.ShowDialog();
            if (!Services.Security.UserSession.IsLoggedIn)
            {
                Shutdown();
                return;
            }
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
}
