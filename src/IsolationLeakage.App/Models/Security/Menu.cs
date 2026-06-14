using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 菜单/权限表（仿若依 sys_menu）
/// </summary>
[Table("Menus")]
public sealed class Menu
{
    [Key]
    public int MenuId { get; set; }

    [Required]
    [MaxLength(50)]
    public string MenuName { get; set; } = string.Empty;

    /// <summary>父菜单 ID（0 = 顶级）</summary>
    public int ParentId { get; set; }

    public int Sort { get; set; }

    [MaxLength(100)]
    public string? Path { get; set; }

    [MaxLength(100)]
    public string? Component { get; set; }

    public SysMenuType Type { get; set; } = SysMenuType.Menu;

    public bool Visible { get; set; } = true;

    /// <summary>权限标识（如 system:user:add）</summary>
    [MaxLength(100)]
    public string? Perms { get; set; }

    [MaxLength(50)]
    public string? Icon { get; set; }

    [MaxLength(500)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // 导航属性
    public Menu? Parent { get; set; }
    public ICollection<Menu> Children { get; set; } = [];
    public ICollection<RoleMenu> RoleMenus { get; set; } = [];

    // 计算属性
    public string TypeText => Type switch
    {
        SysMenuType.Directory => "📁",
        SysMenuType.Menu => "📄",
        SysMenuType.Button => "🔘",
        _ => string.Empty
    };
}

/// <summary>
/// 菜单类型（仿若依 menu_type）
/// </summary>
public enum SysMenuType
{
    Directory = 1,  // 目录
    Menu = 2,       // 菜单
    Button = 3,     // 按钮
}
