namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 动态通道数据（嵌入在 TestProcessData.ChannelsJson 中）。
/// 每个通道包含名称、单位、数据点数组和极值。
/// </summary>
public sealed class ChannelData
{
    /// <summary>显示名称，如"压力P1"、"温度T"、"湿度H"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>单位，如"MPa"、"℃"、"%RH"</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>数据点数组（与各通道等长）</summary>
    public double[] Data { get; set; } = [];

    /// <summary>最小值</summary>
    public double Min { get; set; }

    /// <summary>最大值</summary>
    public double Max { get; set; }
}
