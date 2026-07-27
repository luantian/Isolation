using System.Text.Json.Serialization;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Models;

/// <summary>
/// 试验数据上传载荷（测量装置 → 软件）
/// </summary>
public sealed class DataPayload
{
    /// <summary>试验对象编码</summary>
    public string ObjectCode { get; set; } = string.Empty;

    /// <summary>测量装置编码</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>试验时间</summary>
    public DateTime TestTime { get; set; }

    /// <summary>试验结果（合格/不合格）</summary>
    public TestResult Result { get; set; }

    /// <summary>最终泄漏率（L/h）</summary>
    public decimal FinalLeakageRate { get; set; }

    /// <summary>试验压力（MPa）</summary>
    public decimal TestPressure { get; set; }

    /// <summary>泄漏率限值（L/h）</summary>
    public decimal LeakageLimit { get; set; }

    /// <summary>操作人员</summary>
    public string? Operator { get; set; }

    /// <summary>过程曲线数据</summary>
    public List<DataPoint> ProcessData { get; set; } = [];

    /// <summary>数据包生成时间</summary>
    public DateTime PackageGeneratedAt { get; set; }

    /// <summary>备注</summary>
    public string? Remark { get; set; }

    /// <summary>序列化为 JSON</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, JsonOptions.Default);

    /// <summary>从 JSON 反序列化</summary>
    public static DataPayload? FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<DataPayload>(json, JsonOptions.Default);
}

/// <summary>
/// 过程采集数据点
/// </summary>
public sealed class DataPoint
{
    /// <summary>相对试验开始的时间偏移</summary>
    [JsonIgnore]
    public TimeSpan TimeOffset { get; set; }

    /// <summary>偏移秒数（用于 JSON 序列化）</summary>
    [JsonPropertyName("timeOffsetSeconds")]
    public double TimeOffsetSeconds
    {
        get => TimeOffset.TotalSeconds;
        set => TimeOffset = TimeSpan.FromSeconds(value);
    }

    /// <summary>压力值（MPa）</summary>
    public double? Pressure { get; set; }

    /// <summary>流量值（L/h）</summary>
    public double? FlowRate { get; set; }

    /// <summary>温度值（℃）</summary>
    public double? Temperature { get; set; }
}
