using System;
using System.Collections.Generic;
using System.Windows;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class MeasurementDeviceEditDialog : Window
{
    public MeasurementDevice Device { get; }
    public bool IsEdit { get; }
    public IReadOnlyList<CommunicationType> CommunicationOptions { get; }

    public MeasurementDeviceEditDialog(MeasurementDevice device, bool isEdit)
    {
        InitializeComponent();
        Device = device;
        IsEdit = isEdit;
        CommunicationOptions = Enum.GetValues<CommunicationType>();
        DataContext = this;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Device.DeviceCode))
        {
            MessageBox.Show("装置编号不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(Device.DeviceName))
        {
            MessageBox.Show("装置名称不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
