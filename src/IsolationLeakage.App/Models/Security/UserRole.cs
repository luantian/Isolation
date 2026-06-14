using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 用户-角色关联表（仿若依 sys_user_role）
/// </summary>
[Table("UserRoles")]
public sealed class UserRole
{
    [Key, Column(Order = 0)]
    public int UserId { get; set; }

    [Key, Column(Order = 1)]
    public int RoleId { get; set; }

    // 导航属性
    public User? User { get; set; }
    public Role? Role { get; set; }
}
