using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Configuration;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 数据库上下文工厂（用于运行时创建 DbContext）
/// 支持主从故障切换：当 DatabaseFailoverService 启用且切换后，自动使用活跃连接。
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
    /// 当故障切换启用时，自动使用当前活跃的连接字符串（主库或从库）。
    /// </summary>
    public static AppDbContext CreateDbContext()
    {
        // 测试模式：使用注入的工厂（InMemory DB）
        if (_testFactory != null) return _testFactory();

        var connectionString = GetActiveConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(100));

        return new AppDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// 获取当前活跃的连接字符串。
    /// 如果故障切换服务启用，返回故障切换服务的活跃连接；否则返回默认配置。
    /// </summary>
    public static string GetActiveConnectionString()
    {
        // 如果手动设置了连接字符串，优先使用
        if (_connectionString != null) return _connectionString;

        // 检查故障切换服务是否启用
        var failover = Services.DatabaseFailoverService.Instance;
        if (failover.IsEnabled && !string.IsNullOrWhiteSpace(failover.ActiveConnectionString))
        {
            return failover.ActiveConnectionString;
        }

        // 默认返回主库连接
        return AppConfiguration.GetConnectionString("DefaultConnection");
    }

    /// <summary>
    /// 获取默认连接字符串（主库）
    /// </summary>
    public static string GetDefaultConnectionString()
    {
        return _connectionString ?? AppConfiguration.GetConnectionString("DefaultConnection");
    }
}
