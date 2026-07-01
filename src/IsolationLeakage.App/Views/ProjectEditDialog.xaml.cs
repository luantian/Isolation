using System.Windows;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class ProjectEditDialog : Window
{
    public Project Project { get; set; }

    public ProjectEditDialog(Project project)
    {
        InitializeComponent();
        Project = project;
        DataContext = this;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Project.Name))
        {
            MessageBox.Show("项目名称不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
