using System.Windows;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

/// <summary>
/// RecipeEditDialog.xaml 的交互逻辑
/// </summary>
public partial class RecipeEditDialog : Window
{
    private readonly RecipeEditViewModel _viewModel;

    public RecipeEditDialog(RecipeEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Owner = Application.Current.MainWindow;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
