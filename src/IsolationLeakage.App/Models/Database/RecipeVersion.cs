using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 配方版本历史表
/// 每次修改配方都创建新版本，支持完整审计追溯
/// </summary>
[Table("RecipeVersions")]
public sealed class RecipeVersion
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 关联的配方ID
    /// </summary>
    public int RecipeId { get; set; }

    /// <summary>
    /// 版本号（从1开始递增）
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// 配方名称快照
    /// </summary>
    [MaxLength(100)]
    public string RecipeName { get; set; } = string.Empty;

    /// <summary>
    /// 完整配方参数快照（JSON）
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string RecipeSnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// 版本变更说明
    /// </summary>
    [MaxLength(500)]
    public string? ChangeDescription { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 创建人
    /// </summary>
    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// 是否为当前生效版本
    /// </summary>
    public bool IsCurrentVersion { get; set; }

    /// <summary>
    /// 导航属性：所属配方
    /// </summary>
    public TestRecipe? Recipe { get; set; }

    /// <summary>
    /// 从配方实体创建快照（基于新配方模型）
    /// </summary>
    public static RecipeVersion CreateFromRecipe(TestRecipe recipe, string? changeDescription = null, string? operatorName = null)
    {
        var snapshot = new RecipeSnapshot
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.RecipeName,
            SequenceNo = recipe.SequenceNo,
            System = recipe.System,
            PenetrationDiameter = recipe.PenetrationDiameter,
            ValveNo = recipe.ValveNo,
            ValveNominalDiameter = recipe.ValveNominalDiameter,
            LeakageLimit = recipe.LeakageLimit,
            PrechargePressureP2 = recipe.PrechargePressureP2,
            Remark = recipe.Remark,
            SnapshotTime = DateTime.Now
        };

        return new RecipeVersion
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.RecipeName,
            RecipeSnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false }),
            ChangeDescription = changeDescription,
            CreatedAt = DateTime.Now,
            CreatedBy = operatorName,
            IsCurrentVersion = true
        };
    }
}

/// <summary>
/// 配方快照（用于试验记录保存）
/// </summary>
public sealed class RecipeSnapshot
{
    public int RecipeId { get; set; }
    public string RecipeName { get; set; } = string.Empty;
    public int SequenceNo { get; set; }
    public string System { get; set; } = string.Empty;
    public decimal PenetrationDiameter { get; set; }
    public string ValveNo { get; set; } = string.Empty;
    public decimal ValveNominalDiameter { get; set; }
    public decimal LeakageLimit { get; set; }
    public decimal PrechargePressureP2 { get; set; }
    public string? Remark { get; set; }
    public DateTime SnapshotTime { get; set; }
}
