using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Security;

/// <summary>
/// 操作审计日志（记录所有非登录类操作：CRUD、备份恢复、导入导出等）
/// </summary>
[Table("OperationLogs")]
public sealed class OperationLog
{
    [Key]
    public long LogId { get; set; }

    /// <summary>操作类型（如"创建项目"、"删除用户"、"数据库备份"等）</summary>
    [Required]
    [MaxLength(100)]
    public string OperationType { get; set; } = string.Empty;

    /// <summary>操作用户名</summary>
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>操作详情</summary>
    [MaxLength(1000)]
    public string? Details { get; set; }

    /// <summary>操作结果：Success / 失败原因描述</summary>
    [MaxLength(200)]
    public string Result { get; set; } = string.Empty;

    /// <summary>客户端 IP（可选）</summary>
    [MaxLength(50)]
    public string? IpAddress { get; set; }

    /// <summary>操作时间</summary>
    public DateTime OperationTime { get; set; } = DateTime.Now;
}
