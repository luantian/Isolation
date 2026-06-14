using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验过程数据表（大字段，单独存储，按需加载）
/// </summary>
[Table("TestProcessData")]
public sealed class TestProcessData
{
    [Key]
    [MaxLength(50)]
    public string RecordCode { get; set; } = string.Empty;

    // 压力曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? PressureCurveJson { get; set; }

    // 流量曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? FlowCurveJson { get; set; }

    // 温度曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? TempCurveJson { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal PressureMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal PressureMax { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FlowMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FlowMax { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TempMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TempMax { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：关联的试验记录
    public TestRecord? TestRecord { get; set; }
}
