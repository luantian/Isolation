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
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 根据屏幕宽度调整每页显示条数
        if (DataContext is TestRecordsViewModel vm)
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            vm.PageSize = screenWidth >= 1920 ? 10 : 8;
        }
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 只有点击在数据行上才触发，排除表头、滚动条等
        var dataGrid = sender as DataGrid;
        if (dataGrid == null) return;

        // 获取鼠标位置
        var pos = e.GetPosition(dataGrid);

        // 遍历可视树，找到 DataGridRow
        var hitTestResult = VisualTreeHelper.HitTest(dataGrid, pos);
        if (hitTestResult == null) return;

        var source = hitTestResult.VisualHit;
        while (source != null)
        {
            if (source is DataGridRow row && row.Item != null)
            {
                // 确认是数据行且不是空行
                if (DataContext is TestRecordsViewModel vm && vm.ChangeRecipeCommand.CanExecute(null))
                {
                    vm.ChangeRecipeCommand.Execute(null);
                    e.Handled = true;
                }
                return;
            }
            source = VisualTreeHelper.GetParent(source);
        }
    }

    /// <summary>勾选/取消勾选记录时，通知 ViewModel 刷新命令状态</summary>
    private void OnSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TestRecordsViewModel vm)
        {
            vm.NotifySelectionChanged();
        }
    }
}
