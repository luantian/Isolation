using System.Windows.Controls;
using System.Windows;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class TestObjectPathManagementView : UserControl
{
    private TestObjectPathManagementViewModel ViewModel => (TestObjectPathManagementViewModel)DataContext;

    public TestObjectPathManagementView()
    {
        InitializeComponent();
    }

    private void PathTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is TestObjectPathManagementViewModel viewModel && e.NewValue is TestObjectPathNode node)
        {
            viewModel.SelectedNode = node;
        }
    }

    private void CreateSystem_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowCreateDialog(PathNodeType.System);
    }

    private void CreatePenetration_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowCreateDialog(PathNodeType.Penetration);
    }

    private void CreateValve_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowCreateDialog(PathNodeType.Valve);
    }

    private void CreateOtherComponent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowCreateDialog(PathNodeType.OtherComponent);
    }

    private void Locate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.LocateFirstMatch();
    }

    private void ShowCreateDialog(PathNodeType nodeType)
    {
        if (!ViewModel.CanCreateNode(nodeType))
        {
            return;
        }

        var dialog = new PathNodeEditorDialog(nodeType, ViewModel.GetNextCode(nodeType), ViewModel.SelectedNode)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && dialog.ResultNode is not null)
        {
            ViewModel.AddNode(dialog.ResultNode);
        }
    }
}
