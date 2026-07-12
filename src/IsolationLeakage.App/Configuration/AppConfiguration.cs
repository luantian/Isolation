using System.IO;
using System.Reflection;
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
                    // 从嵌入资源回退读取（先读入 MemoryStream 以保证可 seek）
                    var bytes = GetEmbeddedResourceBytes("appsettings.json");
                    if (bytes != null)
                    {
                        Log.Information("从嵌入资源加载 appsettings.json");
                        builder.AddJsonStream(new MemoryStream(bytes));
                        found = true;
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
        return connStr ?? @"Server=.\SQLEXPRESS;Database=IsolationLeakageDb;Trusted_Connection=True;TrustServerCertificate=True;";
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

        // 从嵌入资源回退读取
        var embeddedPlcBytes = GetEmbeddedResourceBytes("plc-registers.json");
        if (embeddedPlcBytes != null)
        {
            try
            {
                var wrapper = JsonSerializer.Deserialize<PlcRegistersWrapper>(embeddedPlcBytes);
                _plcRegisters = wrapper?.PlcRegisters;
                if (_plcRegisters != null)
                {
                    Log.Information("从嵌入资源加载 plc-registers.json，{Count} 个变量", _plcRegisters.Variables.Count);
                    return _plcRegisters;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "从嵌入资源加载 plc-registers.json 失败");
            }
        }

        // 返回空配置
        _plcRegisters = new PlcRegistersSection();
        Log.Warning("未找到 plc-registers.json，使用空配置");
        return _plcRegisters;
    }

    /// <summary>
    /// 保存连接字符串到 appsettings.json（持久化用户配置的数据库实例）
    /// </summary>
    public static void SaveConnectionString(string name, string connectionString)
    {
        var filePath = FindAppSettingsFile();
        if (filePath == null)
        {
            // 文件不存在，在 BaseDirectory 下新建
            filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        }

        // 读取现有内容（若存在）
        JsonElement root;
        string existingJson = string.Empty;
        if (File.Exists(filePath))
        {
            existingJson = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(existingJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        else
        {
            // 构建空壳
            using var emptyDoc = JsonDocument.Parse("{}");
            root = emptyDoc.RootElement.Clone();
        }

        // 用 Dictionary 重建，更新 ConnectionStrings
        var dict = new Dictionary<string, object?>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "ConnectionStrings")
                    continue; // 下面单独处理
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
        }

        // 合并连接字符串节
        var connDict = new Dictionary<string, string>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ConnectionStrings", out var connSection)
            && connSection.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in connSection.EnumerateObject())
                connDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        connDict[name] = connectionString;
        dict["ConnectionStrings"] = connDict;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var newJson = JsonSerializer.Serialize(dict, options);
        File.WriteAllText(filePath, newJson, System.Text.Encoding.UTF8);

        // 重置 IConfiguration 缓存，使下次读取拿到新值
        _configuration = null;

        Log.Information("已保存连接串 [{Name}] 到 {File}", name, filePath);
    }

    /// <summary>
    /// 获取嵌入资源的流（调用方负责释放）
    /// </summary>
    private static Stream? GetEmbeddedResourceStream(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.{fileName}";
        return assembly.GetManifestResourceStream(resourceName);
    }

    /// <summary>
    /// 读取嵌入资源为字节数组
    /// </summary>
    private static byte[]? GetEmbeddedResourceBytes(string fileName)
    {
        using var stream = GetEmbeddedResourceStream(fileName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 查找 appsettings.json 文件的实际路径
    /// </summary>
    private static string? FindAppSettingsFile()
    {
        var searchPaths = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetDirectoryName(typeof(AppConfiguration).Assembly.Location) ?? string.Empty,
        };
        foreach (var path in searchPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var filePath = Path.Combine(path, "appsettings.json");
            if (File.Exists(filePath)) return filePath;
        }
        return null;
    }

    /// <summary>
    /// 将 JsonElement 递归转为普通对象，以便 System.Text.Json 重新序列化
    /// </summary>
    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => element.GetRawText(),
        };
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
