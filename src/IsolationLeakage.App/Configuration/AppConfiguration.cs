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
}

/// <summary>
/// PLC 寄存器 JSON 包装类（用于反序列化）
/// </summary>
internal class PlcRegistersWrapper
{
    public PlcRegistersSection PlcRegisters { get; set; } = new();
}
