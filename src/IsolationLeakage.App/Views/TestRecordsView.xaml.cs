using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class TestRecordsView : UserControl
{
    private TestRecordsViewModel ViewModel => (TestRecordsViewModel)DataContext;

    public TestRecordsView()
    {
        InitializeComponent();
    }

    private void QueryButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.ApplyQuery();
    }
}
