using System.Text.Json.Serialization;

namespace IsolationLeakage.App.Configuration;

/// <summary>
/// 定期备份配置（持久化到 backup-config.json）
/// </summary>
public class BackupConfig
{
    /// <summary>
    /// 是否启用自动备份
    /// </summary>
    [JsonPropertyName("AutoBackupEnabled")]
    public bool AutoBackupEnabled { get; set; }

    /// <summary>
    /// 自动备份间隔（小时），默认 24
    /// </summary>
    [JsonPropertyName("AutoBackupIntervalHours")]
    public int AutoBackupIntervalHours { get; set; } = 24;

    /// <summary>
    /// 备份保留天数，默认 30
    /// </summary>
    [JsonPropertyName("BackupRetentionPolicyDays")]
    public int BackupRetentionPolicyDays { get; set; } = 30;

    /// <summary>
    /// 备份目录（空表示使用默认目录 BaseDirectory/Backups）
    /// </summary>
    [JsonPropertyName("BackupDirectory")]
    public string BackupDirectory { get; set; } = string.Empty;
}
