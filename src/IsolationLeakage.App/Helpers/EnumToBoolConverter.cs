using System;
using System.Globalization;
using System.Windows.Data;

namespace IsolationLeakage.App.Helpers;

/// <summary>
/// 用于 RadioButton 绑定枚举值的双向转换器。
/// ConverterParameter 传入目标枚举值，当绑定值等于参数时返回 true。
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.Equals(parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null)
            return parameter;
        return Binding.DoNothing;
    }
}
