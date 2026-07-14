using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Configuration;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 数据库上下文工厂（用于运行时创建 DbContext）
/// </summary>
public static class DbContextFactory
{
    private static string? _connectionString;
    private static Func<AppDbContext>? _testFactory;

    /// <summary>
    /// 配置连接字符串（用于运行时覆盖默认配置）
    /// </summary>
    public static void Configure(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 设置测试用工厂（单元测试用，传入 null 恢复默认行为）
    /// </summary>
    public static void SetTestFactory(Func<AppDbContext>? factory)
    {
        _testFactory = factory;
    }

    /// <summary>
    /// 创建 DbContext
    /// </summary>
    public static AppDbContext CreateDbContext()
    {
        // 测试模式：使用注入的工厂（InMemory DB）
        if (_testFactory != null) return _testFactory();

        var connectionString = _connectionString ?? AppConfiguration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(100));

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
