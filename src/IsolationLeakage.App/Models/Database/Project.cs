using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 项目表
/// </summary>
[Table("Projects")]
public sealed class Project
{
    [Key]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public EnabledStatus Status { get; set; } = EnabledStatus.Enabled;

    public string StatusText => Status.ToText();

    [MaxLength(1000)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：项目下的机组
    public ICollection<Unit> Units { get; set; } = [];
}
