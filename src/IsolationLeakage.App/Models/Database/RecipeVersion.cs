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
    /// 配方编码快照
    /// </summary>
    [MaxLength(50)]
    public string RecipeCode { get; set; } = string.Empty;

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
    /// 从配方实体创建快照
    /// </summary>
    public static RecipeVersion CreateFromRecipe(TestRecipe recipe, string? changeDescription = null, string? operatorName = null)
    {
        var snapshot = new RecipeSnapshot
        {
            RecipeId = recipe.Id,
            RecipeCode = recipe.RecipeCode,
            RecipeName = recipe.RecipeName,
            Description = recipe.Description,
            AirtightTargetPressureP1 = recipe.AirtightTargetPressureP1,
            AirtightAllowDropValue = recipe.AirtightAllowDropValue,
            FineBlowTargetPressureP1 = recipe.FineBlowTargetPressureP1,
            PurgeReleasePressure = recipe.PurgeReleasePressure,
            NormalExpectedLeakFlow = recipe.NormalExpectedLeakFlow,
            SmallPrechargeTargetP1 = recipe.SmallPrechargeTargetP1,
            SmallPrechargeTargetP2 = recipe.SmallPrechargeTargetP2,
            MediumPrechargeTargetP1 = recipe.MediumPrechargeTargetP1,
            MediumPrechargeTargetP2 = recipe.MediumPrechargeTargetP2,
            LargePrechargeTargetP1 = recipe.LargePrechargeTargetP1,
            LargePrechargeTargetP2 = recipe.LargePrechargeTargetP2,
            IsEnabled = recipe.IsEnabled,
            SortOrder = recipe.SortOrder,
            SnapshotTime = DateTime.Now
        };

        return new RecipeVersion
        {
            RecipeId = recipe.Id,
            RecipeCode = recipe.RecipeCode,
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
/// 配方参数快照（可序列化对象）
/// </summary>
public sealed class RecipeSnapshot
{
    public int RecipeId { get; set; }
    public string RecipeCode { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string? Description { get; set; }

    // 气密参数
    public decimal AirtightTargetPressureP1 { get; set; }
    public decimal AirtightAllowDropValue { get; set; }

    // 精吹参数
    public decimal FineBlowTargetPressureP1 { get; set; }
    public decimal PurgeReleasePressure { get; set; }

    // 预期流量
    public decimal NormalExpectedLeakFlow { get; set; }

    // 预充压参数
    public decimal SmallPrechargeTargetP1 { get; set; }
    public decimal SmallPrechargeTargetP2 { get; set; }
    public decimal MediumPrechargeTargetP1 { get; set; }
    public decimal MediumPrechargeTargetP2 { get; set; }
    public decimal LargePrechargeTargetP1 { get; set; }
    public decimal LargePrechargeTargetP2 { get; set; }

    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public DateTime SnapshotTime { get; set; }
}
