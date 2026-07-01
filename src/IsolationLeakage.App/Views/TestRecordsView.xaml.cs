using System.Windows.Controls;
using System.Windows.Input;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class TestRecordsView : UserControl
{
    public TestRecordsView()
    {
        InitializeComponent();
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TestRecordsViewModel vm && vm.ChangeRecipeCommand.CanExecute(null))
        {
            vm.ChangeRecipeCommand.Execute(null);
        }
    }

    /// <summary>勾选/取消勾选记录时，通知命令管理器刷新按钮状态</summary>
    private void OnSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
