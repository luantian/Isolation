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
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoggingIn;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ICommand CloseCommand => new RelayCommand(() => CloseLogin());

    /// <summary>
    /// 安全登录方法（由 View 层直接传入 SecureString，避免内存留存）
    /// </summary>
    public async Task<bool> DoLoginSecureAsync(System.Security.SecureString securePassword)
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            ErrorMessage = "请输入用户名";
            return false;
        }

        if (securePassword == null || securePassword.Length == 0)
        {
            ErrorMessage = "请输入密码";
            return false;
        }

        IsLoggingIn = true;
        ErrorMessage = string.Empty;

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
                ErrorMessage = result.Error;
                return false;
            }

            // 初始化会话（包含会话超时配置）
            var roles = await authService.LoadRolesAsync(result.User!.UserId);
            UserSession.Initialize(result.User, roles, result.Permissions);

            // 登录成功
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"登录异常：{ex.Message}";
            MessageBox.Show($"登录详细错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void CloseLogin()
    {
        if (Application.Current.MainWindow is LoginWindow loginWindow)
        {
            loginWindow.DialogResult = false;
            loginWindow.Close();
        }
    }
}
