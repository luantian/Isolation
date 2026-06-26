using System.IO;
using System.Text.Json;
using IsolationLeakage.App.Communication.Models;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace IsolationLeakage.App.Configuration;

/// <summary>
/// 应用配置单例（读取 appsettings.json）
/// </summary>
public static class AppConfiguration
{
    private static IConfiguration? _configuration;

    /// <summary>
    /// 获取 IConfiguration 实例
    /// </summary>
    public static IConfiguration Instance
    {
        get
        {
            if (_configuration == null)
            {
                var builder = new ConfigurationBuilder();
                var searchPaths = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    AppContext.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetDirectoryName(typeof(AppConfiguration).Assembly.Location) ?? string.Empty,
                };

                bool found = false;
                foreach (var path in searchPaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    var filePath = Path.Combine(path, "appsettings.json");
                    if (File.Exists(filePath))
                    {
                        Log.Information("从 {Path} 加载 appsettings.json", filePath);
                        builder.AddJsonFile(filePath, optional: false, reloadOnChange: false);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Log.Warning("未找到 appsettings.json，将使用默认配置");
                }

                _configuration = builder.Build();
            }

            return _configuration;
        }
    }

    /// <summary>
    /// 获取连接字符串
    /// </summary>
    public static string GetConnectionString(string name)
    {
        var connStr = Instance.GetConnectionString(name);
        if (string.IsNullOrEmpty(connStr))
        {
            Log.Warning("配置中未找到连接串 [{Name}]，使用内置默认值", name);
        }
        return connStr ?? @"Server=.\CITADEL;Database=IsolationLeakageDb;Trusted_Connection=True;TrustServerCertificate=True;";
    }

    /// <summary>
    /// 获取配置节
    /// </summary>
    public static IConfigurationSection GetSection(string key)
    {
        return Instance.GetSection(key);
    }

    private static PlcRegistersSection? _plcRegisters;

    /// <summary>
    /// 获取 PLC 寄存器配置（从 plc-registers.json 加载，带缓存）
    /// </summary>
    public static PlcRegistersSection GetPlcRegisters()
    {
        if (_plcRegisters != null) return _plcRegisters;

        var searchPaths = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };

        foreach (var path in searchPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var filePath = Path.Combine(path, "plc-registers.json");
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var wrapper = JsonSerializer.Deserialize<PlcRegistersWrapper>(json);
                    _plcRegisters = wrapper?.PlcRegisters;
                    if (_plcRegisters != null)
                    {
                        Log.Information("从 {Path} 加载 plc-registers.json，{Count} 个变量", filePath, _plcRegisters.Variables.Count);
                        return _plcRegisters;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "加载 plc-registers.json 失败，将使用空配置");
                }
            }
        }

        // 返回空配置
        _plcRegisters = new PlcRegistersSection();
        Log.Warning("未找到 plc-registers.json，使用空配置");
        return _plcRegisters;
    }

    private const string UserSettingsFileName = "user-settings.json";
    private const string LegacyBackupConfigFileName = "backup-config.json";

    private static readonly JsonSerializerOptions UserSettingsJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 加载用户配置（从 user-settings.json，不存在则返回默认值）
    /// 支持从旧的 backup-config.json 自动迁移
    /// </summary>
    public static UserSettings GetUserSettings()
    {
        var searchPaths = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };

        // 先尝试加载新格式
        foreach (var path in searchPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var filePath = Path.Combine(path, UserSettingsFileName);
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var config = JsonSerializer.Deserialize<UserSettings>(json);
                    if (config != null)
                    {
                        Log.Information("从 {Path} 加载 user-settings.json", filePath);
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "加载 user-settings.json 失败，将使用默认配置");
                }
            }
        }

        // 尝试从旧配置迁移
        var migrated = TryMigrateFromLegacyConfig(searchPaths);
        if (migrated != null)
        {
            Log.Information("已从 backup-config.json 自动迁移配置到 user-settings.json");
            SaveUserSettings(migrated);
            return migrated;
        }

        return new UserSettings();
    }

    /// <summary>
    /// 保存用户配置到 user-settings.json
    /// </summary>
    public static void SaveUserSettings(UserSettings settings)
    {
        var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UserSettingsFileName);
        var json = JsonSerializer.Serialize(settings, UserSettingsJsonOptions);
        File.WriteAllText(filePath, json);
        Log.Information("已保存 user-settings.json");
    }

    /// <summary>
    /// 尝试从旧的 backup-config.json 迁移配置
    /// </summary>
    private static UserSettings? TryMigrateFromLegacyConfig(string[] searchPaths)
    {
        foreach (var path in searchPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var filePath = Path.Combine(path, LegacyBackupConfigFileName);
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    // 先尝试反序列化为旧格式
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // 提取旧字段并映射到新结构
                    var settings = new UserSettings();
                    settings.Backup.AutoBackupEnabled = root.GetProperty("AutoBackupEnabled").GetBoolean();
                    settings.Backup.AutoBackupIntervalHours = root.GetProperty("AutoBackupIntervalHours").GetInt32();
                    settings.Backup.BackupRetentionPolicyDays = root.GetProperty("BackupRetentionPolicyDays").GetInt32();
                    settings.Backup.BackupDirectory = root.GetProperty("BackupDirectory").GetString() ?? string.Empty;

                    // 备份旧文件后删除
                    File.Move(filePath, filePath + ".legacy", overwrite: true);
                    Log.Information("旧配置 backup-config.json 已备份为 backup-config.json.legacy");

                    return settings;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "从 backup-config.json 迁移配置失败");
                }
            }
        }
        return null;
    }
}

/// <summary>
/// PLC 寄存器 JSON 包装类（用于反序列化）
/// </summary>
internal class PlcRegistersWrapper
{
    public PlcRegistersSection PlcRegisters { get; set; } = new();
}
