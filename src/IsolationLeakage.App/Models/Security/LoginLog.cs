using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 登录审计日志（安全要求：记录所有登录尝试）
/// </summary>
[Table("LoginLogs")]
public sealed class LoginLog
{
    [Key]
    public long LogId { get; set; }

    /// <summary>
    /// 尝试登录的用户名
    /// </summary>
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// 登录是否成功
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 失败原因
    /// </summary>
    [MaxLength(200)]
    public string? FailReason { get; set; }

    /// <summary>
    /// 客户端IP
    /// </summary>
    [MaxLength(50)]
    public string? ClientIp { get; set; }

    /// <summary>
    /// 登录时间
    /// </summary>
    public DateTime LoginTime { get; set; }

    /// <summary>
    /// 用户代理/设备信息
    /// </summary>
    [MaxLength(500)]
    public string? UserAgent { get; set; }
}
