using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

/// <summary>
/// RecipeManagementView.xaml 的交互逻辑
/// </summary>
public partial class RecipeManagementView : UserControl
{
    public RecipeManagementView()
    {
        InitializeComponent();
        // DataContext 由 App.xaml 的 DataTemplate 注入 MainViewModel 缓存的 RecipeManagementViewModel，
        // 不再自建 VM（否则覆盖注入的实例，导致 ActivePage 刷新的与页面显示的是两个不同 VM）。
        Loaded += async (_, _) =>
        {
            if (DataContext is RecipeManagementViewModel vm)
                await vm.RefreshAsync();
        };
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not RecipeManagementViewModel viewModel) return;

        // 只有点击在数据行上才触发编辑，排除表头
        var dataGrid = sender as DataGrid;
        if (dataGrid == null) return;

        var pos = e.GetPosition(dataGrid);
        var hitTestResult = VisualTreeHelper.HitTest(dataGrid, pos);
        if (hitTestResult == null) return;

        var source = hitTestResult.VisualHit;
        while (source != null)
        {
            if (source is DataGridRow row && row.Item != null)
            {
                viewModel.DoubleClickEditCommand.Execute(null);
                e.Handled = true;
                return;
            }
            source = VisualTreeHelper.GetParent(source);
        }
    }
}
