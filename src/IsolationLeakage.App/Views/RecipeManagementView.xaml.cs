using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        _viewModel.DoubleClickEditCommand.Execute(null);
    }
}
