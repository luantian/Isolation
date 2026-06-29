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

    // ============ 5 通道曲线数据（对应真实装置导出 CSV）============
    // 通道映射：
    //   PressureCurveJson  = 实时压力 P1 (装置地址 MD512, Real)
    //   FlowCurveJson      = 瞬时流量 M1 (装置地址 MW804, UInt)
    //   Flow2CurveJson     = 瞬时流量 M2 (装置地址 MW806, UInt)
    //   TempCurveJson      = 温度 T_R   (装置地址 MD500, Real)
    //   Pressure2CurveJson = 压力 P2_R  (装置地址 MD504, Real)

    // 压力 P1 曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? PressureCurveJson { get; set; }

    // 流量 M1 曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? FlowCurveJson { get; set; }

    // 流量 M2 曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? Flow2CurveJson { get; set; }

    // 温度 T 曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? TempCurveJson { get; set; }

    // 压力 P2 曲线数据：JSON 序列化的 double[]
    [Column(TypeName = "nvarchar(max)")]
    public string? Pressure2CurveJson { get; set; }

    // 时间轴：相对首个采样点的秒数偏移，JSON 序列化的 double[]。
    // 与各通道数组等长，用于过程曲线按真实采集时间展示（而非采样索引）。
    [Column(TypeName = "nvarchar(max)")]
    public string? TimeAxisJson { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal PressureMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal PressureMax { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FlowMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FlowMax { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal Flow2Min { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal Flow2Max { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TempMin { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TempMax { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal Pressure2Min { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal Pressure2Max { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：关联的试验记录
    public TestRecord? TestRecord { get; set; }
}
