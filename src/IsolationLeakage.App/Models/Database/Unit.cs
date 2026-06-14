using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 机组表
/// </summary>
[Table("Units")]
public sealed class Unit
{
    [Key]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    // 外键：关联到项目
    [Required]
    [MaxLength(50)]
    public string ProjectCode { get; set; } = string.Empty;

    public EnabledStatus Status { get; set; } = EnabledStatus.Enabled;

    public string StatusText => Status.ToText();

    [MaxLength(1000)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：所属项目
    public Project? Project { get; set; }

    // 导航属性：机组下的试验对象路径
    public ICollection<TestObjectPathNode> PathNodes { get; set; } = [];
}
