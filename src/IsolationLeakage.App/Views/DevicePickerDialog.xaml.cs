using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

/// <summary>
/// 测量装置选择对话框 —— 导入数据时数据文件缺装置编号（或编号未在台账登记）时弹出，
/// 让用户从已登记装置中手动指定本次导入所属的装置。
/// </summary>
public partial class DevicePickerDialog : Window
{
    /// <summary>用户选中的装置编号（DialogResult=true 时有效）</summary>
    public string? SelectedDeviceCode { get; private set; }

    /// <param name="devices">可选装置列表（应为台账中已登记的启用装置）</param>
    /// <param name="hint">可选的提示文案，说明为什么需要选择</param>
    public DevicePickerDialog(IReadOnlyList<MeasurementDevice> devices, string? hint = null)
    {
        InitializeComponent();

        DeviceComboBox.ItemsSource = devices;
        if (devices.Count > 0)
            DeviceComboBox.SelectedIndex = 0;

        if (!string.IsNullOrWhiteSpace(hint))
            HintText.Text = hint;
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = DeviceComboBox.SelectedItem is MeasurementDevice;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is not MeasurementDevice device)
        {
            MessageBox.Show("请先选择一台测量装置。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedDeviceCode = device.DeviceCode;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
