using System.Windows.Controls;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.ViewModels.Auth;

namespace IsolationLeakage.App.Views.Auth;

public partial class RoleManagementView : UserControl
{
    public RoleManagementView()
    {
        InitializeComponent();
    }

    private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is Role role)
        {
            if (DataContext is RoleManagementViewModel vm)
            {
                vm.SelectRole(role);
            }
        }
    }
}
