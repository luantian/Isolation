using System.IO;
using System.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 初始化 Serilog 日志
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 10_485_760,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

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
                $"数据库初始化失败：{ex.Message}\n\n请确保 SQL Server (实例名: CITADEL) 已启动且可连接。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Log.CloseAndFlush();
            Shutdown();
            return;
        }

        // 设置显式关闭模式，防止 LoginWindow 关闭后 WPF 自动退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 显示登录窗口
        var loginWindow = new LoginWindow { Owner = null };
        MainWindow = loginWindow;
        loginWindow.ShowDialog();

        if (!Services.Security.UserSession.IsLoggedIn)
        {
            // 用户取消登录或关闭登录窗口
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
}
