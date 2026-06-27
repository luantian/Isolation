using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验工艺配方表
/// </summary>
[Table("TestRecipes")]
public sealed class TestRecipe
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 配方编码（唯一）
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string RecipeCode { get; set; } = string.Empty;

    /// <summary>
    /// 配方名称
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string RecipeName { get; set; } = string.Empty;

    /// <summary>
    /// 配方描述/说明
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    #region 气密阶段参数

    /// <summary>
    /// 气密目标压力 P1 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal AirtightTargetPressureP1 { get; set; }

    /// <summary>
    /// 气密允许下降值 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal AirtightAllowDropValue { get; set; }

    #endregion

    #region 精吹阶段参数

    /// <summary>
    /// 精吹目标压力 P1 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal FineBlowTargetPressureP1 { get; set; }

    /// <summary>
    /// 吹扫泄压压力 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal PurgeReleasePressure { get; set; }

    #endregion

    #region 预期泄漏流量阈值

    /// <summary>
    /// 常规预期泄漏流量 (L/min)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal NormalExpectedLeakFlow { get; set; }

    #endregion

    #region 小预充压参数

    /// <summary>
    /// 常规小预充压目标压力 P1 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal SmallPrechargeTargetP1 { get; set; }

    /// <summary>
    /// 常规小预充压目标压力 P2 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal SmallPrechargeTargetP2 { get; set; }

    #endregion

    #region 中预充压参数

    /// <summary>
    /// 常规中预充压目标压力 P1 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal MediumPrechargeTargetP1 { get; set; }

    /// <summary>
    /// 常规中预充压目标压力 P2 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal MediumPrechargeTargetP2 { get; set; }

    #endregion

    #region 大预充压参数

    /// <summary>
    /// 常规大预充压目标压力 P1 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal LargePrechargeTargetP1 { get; set; }

    /// <summary>
    /// 常规大预充压目标压力 P2 (MPa)
    /// </summary>
    [Column(TypeName = "decimal(18, 4)")]
    public decimal LargePrechargeTargetP2 { get; set; }

    #endregion

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

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
