using FluentAssertions;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Tests.Helpers;
using AppModels = IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace IsolationLeakage.App.Tests.Integration;

/// <summary>
/// 业务级集成测试：使用真实 SQL Server (SQLEXPRESS 实例) 验证核心业务流程
/// 测试库：IsolationLeakageDb_Tests
/// </summary>
[Collection("IntegrationTests")]
public class BusinessIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalConnectionString;

    public BusinessIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _originalConnectionString = DbContextFactory.GetDefaultConnectionString();
        // 切换到测试数据库（使用运行中的 SQLEXPRESS 实例）
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = @".\SQLEXPRESS",
            InitialCatalog = "IsolationLeakageDb_Tests",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
        };
        DbContextFactory.Configure(builder.ConnectionString);
    }

    public async Task InitializeAsync()
    {
        TestDbContextHelper.EnsureEncodingsRegistered();
        // 确保测试数据库有基本表结构
        using var ctx = DbContextFactory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        // 清理测试数据
        using var ctx = DbContextFactory.CreateDbContext();
        ctx.TestRecords.RemoveRange(ctx.TestRecords);
        ctx.RecipeVersions.RemoveRange(ctx.RecipeVersions);
        ctx.TestRecipes.RemoveRange(ctx.TestRecipes);
        await ctx.SaveChangesAsync();
    }

    public void Dispose()
    {
        // 恢复原连接字符串
        DbContextFactory.Configure(_originalConnectionString);
    }

    // ── 业务场景 1：导入甲方原始 CSV ──

    [Fact]
    public async Task Business_ImportRealCsv_AllRowsImported()
    {
        // 读取实际的甲方 CSV 文件
        var csvPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "..",
            "doc", "配方组0.csv");

        if (!File.Exists(csvPath))
        {
            _output.WriteLine($"CSV 文件不存在: {csvPath}，跳过测试");
            return;
        }

        var gbk = System.Text.Encoding.GetEncoding("GBK");
        var csvContent = await File.ReadAllTextAsync(csvPath, gbk);

        _output.WriteLine($"CSV 内容:\n{csvContent}");

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csvContent, "test");

        _output.WriteLine($"导入结果: {result.Summary}");
        foreach (var err in result.Errors)
            _output.WriteLine($"  警告: {err}");

        // 关键断言：所有数据行都应导入（不跳过）
        result.TotalProcessed.Should().BeGreaterThan(0, "应该至少导入一些配方");
        result.Skipped.Should().Be(0, "不应该跳过任何行");

        // 验证数据库中的实际数据
        var recipes = await service.GetAllAsync();
        _output.WriteLine($"\n数据库中的配方 ({recipes.Count} 个):");
        foreach (var r in recipes)
            _output.WriteLine($"  - {r.RecipeName} | 序号={r.SequenceNo} | 系统={r.System}");
    }

    // ── 业务场景 2：同名配方的重复导入 ──

    [Fact]
    public async Task Business_ImportTwice_SecondTimeUpdates()
    {
        var service = new RecipeService();
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "测试配方A,1,CAS,10.2,FBDF,10.2,200,0.123";

        // 第一次导入
        var result1 = await service.ImportFromCsvAsync(csv, "test");
        result1.Created.Should().Be(1);

        // 第二次导入（同名不同数据）
        var csv2 = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                   "测试配方A,2,CAM,15.5,FBDF2,15.5,100,0.456";
        var result2 = await service.ImportFromCsvAsync(csv2, "test");

        result2.Updated.Should().Be(1, "第二次导入应更新已有配方");
        result2.Created.Should().Be(0);

        var recipe = await service.GetByNameAsync("测试配方A");
        recipe!.SequenceNo.Should().Be(2);
        recipe.System.Should().Be("CAM");

        // 验证版本历史
        var versions = await service.GetVersionHistoryAsync(recipe.Id);
        versions.Should().HaveCount(2, "应该有2个版本");
    }

    // ── 业务场景 3：导出后重新导入 ──

    [Fact]
    public async Task Business_ExportThenImport_DataIntegrity()
    {
        var service = new RecipeService();

        // 创建测试配方
        await service.CreateAsync(new TestRecipe
        {
            RecipeName = "完整性测试",
            System = "CAS测试系统",
            PenetrationDiameter = 12.5m,
            ValveNo = "测试阀门-V001",
            ValveNominalDiameter = 20.0m,
            LeakageLimit = 150.5m,
            PrechargePressureP2 = 0.25m,
            IsEnabled = true,
            SortOrder = 50,
            Remark = "这是备注，含特殊字符: ,\"中文\"",
        }, "业务测试", "tester");

        // 导出
        var csv = await service.ExportToCsvAsync();
        _output.WriteLine($"导出CSV:\n{csv}");

        // 清空后重新导入
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.TestRecipes.RemoveRange(ctx.TestRecipes);
            ctx.RecipeVersions.RemoveRange(ctx.RecipeVersions);
            await ctx.SaveChangesAsync();
        }

        var result = await service.ImportFromCsvAsync(csv, "test");
        result.Created.Should().Be(1);

        var imported = await service.GetByNameAsync("完整性测试");
        imported.Should().NotBeNull();
        // 排序号不参与 CSV 往返（业务已弃用该字段），不做断言
        imported!.System.Should().Be("CAS测试系统");
        imported.PenetrationDiameter.Should().Be(12.5m);
        imported.ValveNo.Should().Be("测试阀门-V001");
        imported.LeakageLimit.Should().Be(150.5m);
        imported.PrechargePressureP2.Should().Be(0.25m);
        imported.Remark.Should().Contain("这是备注");
    }

    // ── 业务场景 4：备份功能 ──

    [Fact]
    public async Task Business_BackupDatabase_Succeeds()
    {
        var service = new SystemManagementService();
        // 使用 SQL Server 默认备份目录（服务账号保证可写）
        var sqlBackupDir = await SystemManagementService.GetSqlServerDefaultBackupDirAsync();
        var tempPath = Path.Combine(sqlBackupDir, $"test_backup_{Guid.NewGuid()}.bak");

        try
        {
            await service.BackupDatabaseAsync(tempPath);
            File.Exists(tempPath).Should().BeTrue("备份文件应该存在");
            new FileInfo(tempPath).Length.Should().BeGreaterThan(0, "备份文件不应为空");
            _output.WriteLine($"备份成功: {tempPath} ({new FileInfo(tempPath).Length / 1024} KB)");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    // ── 业务场景 5：含特殊字符的 CSV ──

    [Fact]
    public async Task Business_ImportCsvWithSpecialChars_AllImported()
    {
        // 模拟真实业务数据：含逗号、引号、中文
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2,备注\n" +
                  "\"配方A,版本1\",1,CAS,10.2,FBDF,10.2,200,0.123,\"正常配方，含逗号\"\n" +
                  "\"配方B \"\"特别版\"\"\",2,CAM,15.5,FBDF2,15.5,100,0.456,\"备注含\"\"引号\"\"\"\n" +
                  "配方C,3,,8.0,VALVE-003,8.0,50,0.1,";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "test");

        result.TotalProcessed.Should().Be(3);
        result.Skipped.Should().Be(0);

        var recipes = await service.GetAllAsync();
        recipes.Should().HaveCount(3);

        // 验证特殊字符处理
        var a = recipes.First(r => r.RecipeName.Contains("配方A"));
        a.RecipeName.Should().Be("配方A,版本1");
        a.Remark.Should().Contain("含逗号");

        var b = recipes.First(r => r.RecipeName.Contains("配方B"));
        b.RecipeName.Should().Contain("特别版");
    }

    // ── 业务场景 6：空数据库首次导入 ──

    [Fact]
    public async Task Business_FirstImport_EmptyDatabase()
    {
        // 确保数据库是空的
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.TestRecipes.RemoveRange(ctx.TestRecipes);
            ctx.RecipeVersions.RemoveRange(ctx.RecipeVersions);
            await ctx.SaveChangesAsync();
        }

        var service = new RecipeService();
        var allBefore = await service.GetAllAsync();
        allBefore.Should().BeEmpty("测试前数据库应该是空的");

        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "首次导入,1,CAS,10.0,V001,10.0,100,0.1";

        var result = await service.ImportFromCsvAsync(csv, "first-importer");

        result.Created.Should().Be(1);
        result.Updated.Should().Be(0);

        var recipe = await service.GetByNameAsync("首次导入");
        recipe.Should().NotBeNull();
        recipe!.CreatedBy.Should().Be("first-importer");
    }

    // ── 业务场景 7：装置状态只显示启用装置 ──

    [Fact]
    public async Task Business_DeviceOverview_OnlyShowsEnabled()
    {
        // 创建启用和停用的装置
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.MeasurementDevices.RemoveRange(ctx.MeasurementDevices);
            await ctx.SaveChangesAsync();

            ctx.MeasurementDevices.AddRange(
                new MeasurementDevice { DeviceCode = "DEV-001", DeviceName = "启用装置", EnabledStatus = AppModels.EnabledStatus.Enabled },
                new MeasurementDevice { DeviceCode = "DEV-002", DeviceName = "停用装置1", EnabledStatus = AppModels.EnabledStatus.Disabled },
                new MeasurementDevice { DeviceCode = "DEV-003", DeviceName = "停用装置2", EnabledStatus = AppModels.EnabledStatus.Disabled }
            );
            await ctx.SaveChangesAsync();
        }

        // 模拟首页概览的查询逻辑
        using var readCtx = DbContextFactory.CreateDbContext();
        var enabledDevices = await readCtx.MeasurementDevices
            .Where(d => d.EnabledStatus == AppModels.EnabledStatus.Enabled)
            .ToListAsync();

        enabledDevices.Should().HaveCount(1, "首页装置状态应只显示启用的装置");
        enabledDevices[0].DeviceCode.Should().Be("DEV-001");

        // 清理
        using var cleanCtx = DbContextFactory.CreateDbContext();
        cleanCtx.MeasurementDevices.RemoveRange(cleanCtx.MeasurementDevices);
        await cleanCtx.SaveChangesAsync();
    }
}
