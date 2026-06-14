using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class ProjectUnitManagementView : UserControl
{
    private ProjectUnitManagementViewModel ViewModel => (ProjectUnitManagementViewModel)DataContext;

    public ProjectUnitManagementView()
    {
        InitializeComponent();
    }

    private async void AddProject_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await ViewModel.AddProjectAsync();
    }

    private async void AddUnit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await ViewModel.AddUnitAsync();
    }
}
