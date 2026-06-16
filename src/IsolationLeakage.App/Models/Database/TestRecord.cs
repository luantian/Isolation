using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验记录表
/// </summary>
[Table("TestRecords")]
public sealed class TestRecord
{
    [Key]
    [MaxLength(50)]
    public string RecordCode { get; set; } = string.Empty;

    /// <summary>
    /// 列表序号（非数据库字段，仅用于 UI 显示）
    /// </summary>
    [NotMapped]
    public int RowNumber { get; set; }

    // 外键：所属项目
    [Required]
    [MaxLength(50)]
    public string ProjectCode { get; set; } = string.Empty;

    // 外键：所属机组
    [Required]
    [MaxLength(50)]
    public string UnitCode { get; set; } = string.Empty;

    // 外键：试验对象（阀门/部件）
    [Required]
    [MaxLength(100)]
    public string ObjectCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string ObjectName { get; set; } = string.Empty;

    public PathNodeType ObjectType { get; set; }

    // 外键：测量装置
    [Required]
    [MaxLength(50)]
    public string DeviceCode { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? DataPackageName { get; set; }

    public DateTime TestTime { get; set; }

    public DateTime ImportTime { get; set; }

    [MaxLength(100)]
    public string Operator { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TestPressure { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal LeakageLimit { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FinalLeakageRate { get; set; }

    public TestResult Result { get; set; }

    /// <summary>
    /// 结果中文显示
    /// </summary>
    [NotMapped]
    public string ResultText => Result == TestResult.Pass ? "合格" : "不合格";

    [MaxLength(2000)]
    public string? Remark { get; set; }

    [MaxLength(1000)]
    public string? StepSummary { get; set; }

    [MaxLength(1000)]
    public string? ResultFieldSummary { get; set; }

    [MaxLength(1000)]
    public string? ProcessChannelSummary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // 显示属性（通过导航属性获取）
    public string? ProjectName => Project?.Name;
    public string? UnitName => Unit?.Name;

    // 导航属性：所属项目
    public Project? Project { get; set; }

    // 导航属性：所属机组
    public Unit? Unit { get; set; }

    // 导航属性：试验对象
    public TestObjectPathNode? TestObject { get; set; }

    // 导航属性：测量装置
    public MeasurementDevice? Device { get; set; }

    // 导航属性：过程数据（一对一）
    public TestProcessData? ProcessData { get; set; }
}
