using System.Windows.Controls;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class ProjectUnitManagementView : UserControl
{
    private ProjectUnitManagementViewModel ViewModel => (ProjectUnitManagementViewModel)DataContext;

    public ProjectUnitManagementView()
    {
        InitializeComponent();
        DataContext = new ProjectUnitManagementViewModel(AppServices.MasterData);
    }

    private void AddProject_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.AddProject();
    }

    private void AddUnit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.AddUnit();
    }
}
