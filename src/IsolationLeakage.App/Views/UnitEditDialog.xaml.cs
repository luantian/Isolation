using System.Windows;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class UnitEditDialog : Window
{
    public Unit Unit { get; set; }

    public UnitEditDialog(Unit unit)
    {
        InitializeComponent();
        Unit = unit;
        DataContext = this;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Unit.Name))
        {
            MessageBox.Show("机组名称不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
