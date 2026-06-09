using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class MeasurementDeviceLedgerView : UserControl
{
    private MeasurementDeviceLedgerViewModel ViewModel => (MeasurementDeviceLedgerViewModel)DataContext;

    public MeasurementDeviceLedgerView()
    {
        InitializeComponent();
    }

    private void Query_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.ApplyQuery();
    }

    private void NewDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.StartNew();
    }

    private void SaveDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.Save();
    }

    private void EnableDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.EnableSelected();
    }

    private void DisableDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.DisableSelected();
    }
}
