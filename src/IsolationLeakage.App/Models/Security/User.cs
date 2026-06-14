using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 用户表（仿若依 sys_user）
/// </summary>
[Table("Users")]
public sealed class User
{
    [Key]
    public int UserId { get; set; }

    [Required]
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? NickName { get; set; }

    [Required]
    [MaxLength(100)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Avatar { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(50)]
    public string? Dept { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Enabled;

    public DateTime? LastLoginTime { get; set; }

    public int LoginCount { get; set; }

    /// <summary>
    /// 连续登录失败次数（安全要求：防止暴力破解）
    /// </summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>
    /// 账户锁定截止时间（null 表示未锁定）
    /// </summary>
    public DateTime? LockoutEnd { get; set; }

    [MaxLength(500)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性
    public ICollection<UserRole> UserRoles { get; set; } = [];

    // 计算属性
    public string StatusText => Status.ToText();
}

/// <summary>
/// 用户状态
/// </summary>
public enum UserStatus
{
    Disabled = 0,
    Enabled = 1,
}

public static class UserStatusExtensions
{
    public static string ToText(this UserStatus status) => status switch
    {
        UserStatus.Enabled => "启用",
        UserStatus.Disabled => "停用",
        _ => "未知"
    };
}
