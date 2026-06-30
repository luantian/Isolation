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
}
