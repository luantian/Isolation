using System.Text;
using IsolationLeakage.App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IsolationLeakage.App.Tests.Helpers;

/// <summary>
/// 测试用 InMemory DbContext 辅助类
/// </summary>
public static class TestDbContextHelper
{
    private static bool _encodingRegistered;

    /// <summary>
    /// 注册 CodePages 编码（GBK 等中文编码在 .NET Core 默认不可用）
    /// </summary>
    public static void EnsureEncodingsRegistered()
    {
        if (!_encodingRegistered)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _encodingRegistered = true;
        }
    }
    /// <summary>
    /// 创建独立的 InMemory DbContext（每个测试用唯一数据库名保证隔离）
    /// </summary>
    public static AppDbContext CreateInMemoryContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            // InMemory 不支持事务，忽略相关警告（RecipeService 使用了 BeginTransactionAsync）
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// 设置全局测试工厂（让 Service 层使用 InMemory DB）
    /// </summary>
    public static void SetupTestFactory(string sharedDbName = "SharedTestDb")
    {
        DbContextFactory.SetTestFactory(() => CreateInMemoryContext(sharedDbName));
    }

    /// <summary>
    /// 恢复默认工厂
    /// </summary>
    public static void ResetTestFactory()
    {
        DbContextFactory.SetTestFactory(null);
    }
}
