using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class SystemManagementView : UserControl
{
    public SystemManagementView()
    {
        InitializeComponent();
    }

    private void OperationLogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SystemManagementViewModel vm)
        {
            vm.OperationLogPage.ViewDetailCommand.Execute(null);
        }
    }
}
