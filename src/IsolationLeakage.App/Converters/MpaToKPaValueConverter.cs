using System.Globalization;
using System.Windows.Data;

namespace IsolationLeakage.App.Converters;

/// <summary>
/// MPa(存储) ↔ kPa(显示) 双向值转换器：
/// Convert：存储值 MPa ×1000 显示；ConvertBack：输入值 kPa ÷1000 存储。
/// 用于 XAML 绑定 DB 中以 MPa 存储的压力值（如配方预充压P2）。
/// </summary>
public sealed class MpaToKPaValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            decimal d => (double)d * 1000.0,
            double d => d * 1000.0,
            float f => (double)f * 1000.0,
            int i => i * 1000.0,
            _ => 0.0,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return 0m;
        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, culture, out var kpa))
        {
            return 0m;
        }
        return (decimal)(kpa / 1000.0);
    }
}
