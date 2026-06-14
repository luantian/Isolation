using System.Text.Json.Serialization;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Models;

/// <summary>
/// 试验任务下发载荷（软件 → 测量装置）
/// </summary>
public sealed class TaskPayload
{
    /// <summary>任务 ID（由软件生成）</summary>
    public string TaskId { get; set; } = $"TASK-{DateTime.Now:yyyyMMddHHmmss}";

    /// <summary>试验对象列表</summary>
    public List<TestObjectEntry> Objects { get; set; } = [];

    /// <summary>任务生成时间</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>操作人员</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>备注</summary>
    public string? Remark { get; set; }

    /// <summary>序列化为 JSON</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, JsonOptions.Default);

    /// <summary>从 JSON 反序列化</summary>
    public static TaskPayload? FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TaskPayload>(json, JsonOptions.Default);
}

/// <summary>
/// 试验对象条目
/// </summary>
public sealed class TestObjectEntry
{
    /// <summary>对象编码（如 1RHR040VP）</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>对象名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>对象类型（阀门/贯穿件/其他部件）</summary>
    public PathNodeType ObjectType { get; set; }

    /// <summary>泄漏率限值（L/min）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? LeakageLimit { get; set; }

    /// <summary>试验压力（MPa）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TestPressure { get; set; }

    /// <summary>阀门类型</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValveType { get; set; }

    /// <summary>部件类型</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentType { get; set; }
}

/// <summary>
/// JSON 序列化选项
/// </summary>
internal static class JsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
