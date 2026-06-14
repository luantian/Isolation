using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Configuration;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 数据库上下文工厂（用于运行时创建 DbContext）
/// </summary>
public static class DbContextFactory
{
    private static string? _connectionString;

    /// <summary>
    /// 配置连接字符串（用于运行时覆盖默认配置）
    /// </summary>
    public static void Configure(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 创建 DbContext
    /// </summary>
    public static AppDbContext CreateDbContext()
    {
        var connectionString = _connectionString ?? AppConfiguration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new AppDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// 获取默认连接字符串
    /// </summary>
    public static string GetDefaultConnectionString()
    {
        return _connectionString ?? AppConfiguration.GetConnectionString("DefaultConnection");
    }
}
