using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 动态趋势通道：实时监控用，每个监控变量对应一条曲线 + 一个图例项。
/// 支持任意数量、可增删，曲线和图例都由它驱动。
/// </summary>
public sealed class TrendChannel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _unit = string.Empty;
    private string _currentValue = "-";
    private Color _color = Colors.DodgerBlue;

    /// <summary>通道名称（变量名），显示在图例</summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>单位（显示在图例）</summary>
    public string Unit
    {
        get => _unit;
        set { _unit = value; OnPropertyChanged(); }
    }

    /// <summary>当前值文本（显示在图例）</summary>
    public string CurrentValue
    {
        get => _currentValue;
        set { _currentValue = value; OnPropertyChanged(); }
    }

    /// <summary>曲线最小值（显示在图例）</summary>
    private double _min;
    public double Min
    {
        get => _min;
        set { _min = value; OnPropertyChanged(); }
    }

    /// <summary>曲线最大值（显示在图例）</summary>
    private double _max;
    public double Max
    {
        get => _max;
        set { _max = value; OnPropertyChanged(); }
    }

    /// <summary>曲线/图例颜色</summary>
    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Brush));
        }
    }

    /// <summary>图例色块画刷</summary>
    public SolidColorBrush Brush => new(_color);

    /// <summary>曲线数据点（Y 值），与图表共享，增量追加</summary>
    public ObservableCollection<double> Points { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
