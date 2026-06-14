using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IsolationLeakage.App.ViewModels.Auth;

namespace IsolationLeakage.App.Views.Auth;

/// <summary>
/// LoginWindow.xaml 交互逻辑
/// 安全设计：密码不通过 DataBinding 传递，直接获取 SecureString
/// 避免密码以明文 string 形式进入 ViewModel 和 INotifyPropertyChanged 管道
/// </summary>
public partial class LoginWindow : Window
{
    private bool _isLoggingIn;

    public LoginWindow()
    {
        InitializeComponent();
        // 开发阶段默认密码，生产环境删除
        PwdBox.Password = "admin123";

        // 开发阶段自动登录，生产环境删除
        Loaded += async (_, _) =>
        {
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);
            await System.Threading.Tasks.Task.Delay(300);
            await ExecuteLoginAsync();
        };
    }

    /// <summary>
    /// 安全登录：直接从 PasswordBox 获取 SecureString
    /// </summary>
    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteLoginAsync();
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PwdBox.Focus();
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ExecuteLoginAsync();
        }
    }

    /// <summary>
    /// 执行登录逻辑（防重入）
    /// </summary>
    private async Task ExecuteLoginAsync()
    {
        if (_isLoggingIn) return;
        if (DataContext is not LoginViewModel vm) return;

        _isLoggingIn = true;
        try
        {
            using SecureString securePwd = PwdBox.SecurePassword.Copy();
            var success = await vm.DoLoginSecureAsync(securePwd);

            if (success)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"登录错误：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoggingIn = false;
        }
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            this.DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
