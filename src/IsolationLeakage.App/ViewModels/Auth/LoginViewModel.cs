using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services.Security;
using IsolationLeakage.App.Views.Auth;
using Microsoft.Data.SqlClient;
namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 登录页面视图模型
/// 安全设计：
/// - 不存储明文密码，使用 SecureString
/// - 密码验证后立即清理内存
/// - 支持会话超时
/// </summary>
public partial class LoginViewModel : IsolationLeakage.App.ViewModels.ViewModelBase
{
    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string _errorDetail = string.Empty;

    [ObservableProperty]
    private bool _isLoggingIn;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    public ICommand CloseCommand => new RelayCommand(() => CloseLogin());

    /// <summary>
    /// 安全登录方法（由 View 层直接传入 SecureString，避免内存留存）
    /// </summary>
    public async Task<bool> DoLoginSecureAsync(System.Security.SecureString securePassword)
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            SetError("请输入用户名");
            return false;
        }

        if (securePassword == null || securePassword.Length == 0)
        {
            SetError("请输入密码");
            return false;
        }

        IsLoggingIn = true;
        ClearError();

        string? plainPassword = null;
        IntPtr ptr = IntPtr.Zero;

        try
        {
            // 仅在验证瞬间解密密码，验证后立即清理
            ptr = Marshal.SecureStringToBSTR(securePassword);
            plainPassword = Marshal.PtrToStringBSTR(ptr);

            using var context = DbContextFactory.CreateDbContext();
            var authService = new AuthService(context);
            var result = await authService.LoginAsync(UserName.Trim(), plainPassword);

            if (!result.IsSuccess)
            {
                SetError(result.Error);
                return false;
            }

            // 初始化会话（包含会话超时配置）
            var roles = await authService.LoadRolesAsync(result.User!.UserId);
            UserSession.Initialize(result.User, roles, result.Permissions);

            // 登录成功
            return true;
        }
        catch (SqlException sqlEx)
        {
            SetDatabaseError(sqlEx);
            return false;
        }
        catch (Exception ex)
        {
            SetError("登录失败", ex.Message);
            return false;
        }
        finally
        {
            // 安全清理：立即释放密码内存
            if (ptr != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(ptr);
            }
            IsLoggingIn = false;
        }
    }

    private void SetError(string message, string? detail = null)
    {
        ErrorMessage = message;
        ErrorDetail = detail ?? string.Empty;
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        ErrorDetail = string.Empty;
    }

    /// <summary>
    /// 将数据库异常翻译为友好提示
    /// </summary>
    private void SetDatabaseError(SqlException sqlEx)
    {
        // 常见 SQL Server 错误号：
        //   2    = 连接不上服务器（网络不通/服务未启动）
        //   53   = 找不到服务器（名称解析失败/服务未启动）
        //   4060 = 数据库不存在
        //   18456 = 登录失败（用户/密码错误，但走不到这里，AuthService 已处理）
        //   -2   = 超时（服务器无响应）
        var (title, detail) = sqlEx.Number switch
        {
            2 or 53 =>
                ("无法连接到数据库服务器",
                 "请检查：\n• 数据库服务器是否已启动\n• 服务器地址是否正确\n• 网络是否连通\n• 防火墙是否放行 1433 端口"),
            4060 =>
                ("数据库不存在",
                 $"无法打开数据库。\n请联系管理员确认数据库是否已创建。\n\n详细：{sqlEx.Message}"),
            -2 =>
                ("连接数据库超时",
                 "服务器响应太慢或网络不稳定。\n请稍后重试，或联系管理员检查数据库服务器状态。"),
            18456 =>
                ("数据库认证失败",
                 "SQL Server 拒绝了登录请求。\n请检查 appsettings.json 中的用户名和密码。"),
            _ =>
                ("数据库连接失败",
                 $"{sqlEx.Message}")
        };

        ErrorMessage = title;
        ErrorDetail = detail;
    }

    private void CloseLogin()
    {
        if (Application.Current.MainWindow is LoginWindow loginWindow)
        {
            loginWindow.DialogResult = false;
            loginWindow.Close();
        }
    }
}
