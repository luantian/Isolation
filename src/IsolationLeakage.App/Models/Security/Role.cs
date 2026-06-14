using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 角色表（仿若依 sys_role）
/// </summary>
[Table("Roles")]
public sealed class Role
{
    [Key]
    public int RoleId { get; set; }

    [Required]
    [MaxLength(50)]
    public string RoleName { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string RoleKey { get; set; } = string.Empty;

    public int Sort { get; set; }

    public DataScope DataScope { get; set; } = DataScope.All;

    public UserStatus Status { get; set; } = UserStatus.Enabled;

    [MaxLength(500)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // 导航属性
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RoleMenu> RoleMenus { get; set; } = [];

    // 计算属性
    public string StatusText => Status.ToText();
    public string DataScopeText => DataScope switch
    {
        DataScope.All => "全部数据",
        DataScope.Dept => "本部门数据",
        DataScope.DeptAndChildren => "本部门及以下",
        DataScope.Self => "仅本人数据",
        DataScope.Custom => "自定义",
        _ => "-"
    };
}

/// <summary>
/// 数据范围（仿若依 data_scope）
/// </summary>
public enum DataScope
{
    All = 1,           // 全部数据
    Dept = 2,          // 本部门数据
    DeptAndChildren = 3, // 本部门及子部门数据
    Self = 4,          // 仅本人数据
    Custom = 5,        // 自定义
}
