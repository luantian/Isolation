using System.Text.Json.Serialization;

namespace IsolationLeakage.App.Configuration;

/// <summary>
/// 用户配置（持久化到 user-settings.json，运行时可修改）
/// </summary>
public class UserSettings
{
    /// <summary>
    /// 备份相关配置
    /// </summary>
    [JsonPropertyName("Backup")]
    public BackupSettings Backup { get; set; } = new();

    /// <summary>
    /// 报告导出相关配置
    /// </summary>
    [JsonPropertyName("Export")]
    public ExportSettings Export { get; set; } = new();
}

/// <summary>
/// 报告导出配置
/// </summary>
public class ExportSettings
{
    /// <summary>
    /// 导出目录（空表示使用默认目录：我的文档）
    /// </summary>
    [JsonPropertyName("ExportDirectory")]
    public string ExportDirectory { get; set; } = string.Empty;
}

/// <summary>
/// 自动备份配置
/// </summary>
public class BackupSettings
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
