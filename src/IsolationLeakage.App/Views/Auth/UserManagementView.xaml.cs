using System.Windows.Controls;
using IsolationLeakage.App.Controls;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.ViewModels.Auth;

namespace IsolationLeakage.App.Views.Auth;

public partial class UserManagementView : UserControl
{
    public UserManagementView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UserManagementViewModel vm)
        {
            vm.OnShowToast += (msg, type) =>
            {
                switch (type)
                {
                    case ToastType.Success: UserToast.ShowSuccess(msg); break;
                    case ToastType.Error: UserToast.ShowError(msg); break;
                    case ToastType.Warning: UserToast.ShowWarning(msg); break;
                }
            };
        }
    }

    private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is User user)
        {
            if (DataContext is UserManagementViewModel vm)
            {
                vm.SelectUser(user);
            }
        }
    }
}
