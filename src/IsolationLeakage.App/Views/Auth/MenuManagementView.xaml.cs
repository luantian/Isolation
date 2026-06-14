using System.Windows;
using System.Windows.Controls;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.ViewModels.Auth;

namespace IsolationLeakage.App.Views.Auth;

public partial class MenuManagementView : UserControl
{
    public MenuManagementView()
    {
        InitializeComponent();
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Models.Security.Menu menu)
        {
            if (DataContext is MenuManagementViewModel vm)
            {
                vm.SelectMenu(menu);
            }
        }
    }
}
