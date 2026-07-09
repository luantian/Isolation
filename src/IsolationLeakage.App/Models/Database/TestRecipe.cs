using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验工艺配方表（基于甲方配方组0.csv格式）
/// </summary>
[Table("TestRecipes")]
public sealed class TestRecipe
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 配方名称（唯一标识）
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string RecipeName { get; set; } = string.Empty;

    /// <summary>
    /// 序号（组内排序号）
    /// </summary>
    public int SequenceNo { get; set; }

    /// <summary>
    /// 系统（CAS/CAM/AAA等）
    /// </summary>
    [MaxLength(50)]
    public string System { get; set; } = string.Empty;

    /// <summary>
    /// 贯穿件直径（mm）
    /// </summary>
    [Column(TypeName = "decimal(18, 2)")]
    public decimal PenetrationDiameter { get; set; }

    /// <summary>
    /// 试验阀门编号
    /// </summary>
    [MaxLength(100)]
    public string ValveNo { get; set; } = string.Empty;

    /// <summary>
    /// 阀门公称直径（mm）
    /// </summary>
    [Column(TypeName = "decimal(18, 2)")]
    public decimal ValveNominalDiameter { get; set; }

    /// <summary>
    /// 阀门泄漏率设计最大值（合格标准）
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal LeakageLimit { get; set; }

    /// <summary>
    /// 预充压压力P2（MPa）
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal PrechargePressureP2 { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 排序号（用于界面显示排序）
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 备注/说明
    /// </summary>
    [MaxLength(500)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [MaxLength(50)]
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// 导航属性：使用此配方的试验记录
    /// </summary>
    public ICollection<TestRecord> TestRecords { get; set; } = new List<TestRecord>();
}
