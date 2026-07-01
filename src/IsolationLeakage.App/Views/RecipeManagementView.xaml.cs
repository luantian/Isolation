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
    private readonly RecipeManagementViewModel _viewModel;

    public RecipeManagementView()
    {
        InitializeComponent();
        _viewModel = new RecipeManagementViewModel();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
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
                _viewModel.DoubleClickEditCommand.Execute(null);
                e.Handled = true;
                return;
            }
            source = VisualTreeHelper.GetParent(source);
        }
    }
}
