using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class TestRecordsView : UserControl
{
    private readonly TestRecordsViewModel _viewModel = new();

    public TestRecordsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void QueryButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewModel.ApplyQuery();
    }
}
