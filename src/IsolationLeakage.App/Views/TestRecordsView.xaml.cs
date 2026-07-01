using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        // 检查是否点击在数据行上（而不是表头）
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is DataGridRow)
            {
                // 确认是在数据行上，执行命令
                if (DataContext is TestRecordsViewModel vm && vm.ChangeRecipeCommand.CanExecute(null))
                {
                    vm.ChangeRecipeCommand.Execute(null);
                }
                return;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
    }

    /// <summary>勾选/取消勾选记录时，通知命令管理器刷新按钮状态</summary>
    private void OnSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
