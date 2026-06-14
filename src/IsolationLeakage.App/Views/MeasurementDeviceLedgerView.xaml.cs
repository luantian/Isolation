using System.Windows;
using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class MeasurementDeviceLedgerView : UserControl
{
    private MeasurementDeviceLedgerViewModel? ViewModel => DataContext as MeasurementDeviceLedgerViewModel;

    public MeasurementDeviceLedgerView()
    {
        InitializeComponent();
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.ApplyQueryAsync();
    }

    private void NewDevice_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.StartNew();
    }

    private async void SaveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.SaveAsync();
    }

    private async void EnableDevice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.EnableSelectedAsync();
    }

    private async void DisableDevice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) await ViewModel.DisableSelectedAsync();
    }
}
