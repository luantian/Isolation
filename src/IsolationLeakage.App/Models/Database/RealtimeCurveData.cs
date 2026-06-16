using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 实时监视曲线数据表（独立存储，不与试验记录绑定）
/// </summary>
[Table("RealtimeCurveData")]
public sealed class RealtimeCurveData
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 监视会话编码（格式：RT-yyyyMMdd-HHmmss）
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SessionCode { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? ProjectCode { get; set; }

    [MaxLength(50)]
    public string? UnitCode { get; set; }

    [MaxLength(100)]
    public string? ObjectCode { get; set; }

    /// <summary>
    /// 可选关联的试验记录编码（监视结束后关联）
    /// </summary>
    [MaxLength(50)]
    public string? RecordCode { get; set; }

    /// <summary>
    /// 压力曲线数据：JSON 序列化的 double[]
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string? PressureCurveJson { get; set; }

    /// <summary>
    /// 流量曲线数据：JSON 序列化的 double[]
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string? FlowCurveJson { get; set; }

    /// <summary>
    /// 温度曲线数据：JSON 序列化的 double[]
    /// </summary>
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

    /// <summary>
    /// 采样间隔（毫秒）
    /// </summary>
    public int SampleIntervalMs { get; set; }

    /// <summary>
    /// 总采样点数
    /// </summary>
    public int PointCount { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime? EndedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [MaxLength(100)]
    public string? Operator { get; set; }
}
