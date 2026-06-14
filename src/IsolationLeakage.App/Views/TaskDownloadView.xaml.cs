using System.Windows.Controls;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class TaskDownloadView : UserControl
{
    public TaskDownloadView()
    {
        InitializeComponent();
    }

    private void PathTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is TaskDownloadViewModel vm && e.NewValue is TestObjectPathNode node)
        {
            vm.SelectedNode = node;
        }
    }
}
