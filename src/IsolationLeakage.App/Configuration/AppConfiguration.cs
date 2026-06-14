using System.IO;
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
}
