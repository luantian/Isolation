using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

/// <summary>
/// 通信方式枚举转文本显示
/// </summary>
public sealed class CommunicationTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CommunicationType type)
        {
            return type.ToText();
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public partial class MeasurementDeviceLedgerView : UserControl
{
    public MeasurementDeviceLedgerView()
    {
        InitializeComponent();
    }
}
