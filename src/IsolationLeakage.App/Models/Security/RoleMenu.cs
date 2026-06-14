using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 角色-菜单关联表（仿若依 sys_role_menu）
/// </summary>
[Table("RoleMenus")]
public sealed class RoleMenu
{
    [Key, Column(Order = 0)]
    public int RoleId { get; set; }

    [Key, Column(Order = 1)]
    public int MenuId { get; set; }

    // 导航属性
    public Role? Role { get; set; }
    public Menu? Menu { get; set; }
}
