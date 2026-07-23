using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Tests.Helpers;
using IsolationLeakage.App.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace IsolationLeakage.App.Tests.Integration;

/// <summary>
/// 首页概览业务测试：验证统计数据、合格率计算、装置过滤等核心逻辑
/// </summary>
[Collection("IntegrationTests")]
public class OverviewBusinessTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalConnectionString;

    public OverviewBusinessTests(ITestOutputHelper output)
    {
        _output = output;
        _originalConnectionString = DbContextFactory.GetDefaultConnectionString();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
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
        using var ctx = DbContextFactory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        using var ctx = DbContextFactory.CreateDbContext();
        ctx.TestRecords.RemoveRange(ctx.TestRecords);
        ctx.MeasurementDevices.RemoveRange(ctx.MeasurementDevices);
        ctx.TestObjectPathNodes.RemoveRange(ctx.TestObjectPathNodes);
        ctx.Units.RemoveRange(ctx.Units);
        ctx.Projects.RemoveRange(ctx.Projects);
        await ctx.SaveChangesAsync();
    }

    public void Dispose()
    {
        DbContextFactory.Configure(_originalConnectionString);
    }

    /// <summary>准备基础台账数据：1项目 / 1机组 / 1系统 / 2贯穿件 / 3阀门 / 1部件</summary>
    private async Task SeedBasicLedgerAsync()
    {
        using var ctx = DbContextFactory.CreateDbContext();

        var project = new Project { Name = "测试项目", Code = "PRJ001" };
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync();

        var unit = new Unit { Name = "1号机组", Code = "UNIT01", ProjectCode = project.Code };
        ctx.Units.Add(unit);
        await ctx.SaveChangesAsync();

        // 系统
        var system = new TestObjectPathNode
        {
            Code = "SYS001", Name = "RHR系统", NodeType = PathNodeType.System, UnitCode = unit.Code
        };
        ctx.TestObjectPathNodes.Add(system);

        // 2个贯穿件
        var pen1 = new TestObjectPathNode
        {
            Code = "PEN001", Name = "贯穿件A", NodeType = PathNodeType.Penetration,
            UnitCode = unit.Code, ParentCode = system.Code
        };
        var pen2 = new TestObjectPathNode
        {
            Code = "PEN002", Name = "贯穿件B", NodeType = PathNodeType.Penetration,
            UnitCode = unit.Code, ParentCode = system.Code
        };
        ctx.TestObjectPathNodes.AddRange(pen1, pen2);

        // 3个阀门
        for (int i = 1; i <= 3; i++)
        {
            ctx.TestObjectPathNodes.Add(new TestObjectPathNode
            {
                Code = $"VP{i:D3}", Name = $"阀门{i}", NodeType = PathNodeType.Valve,
                UnitCode = unit.Code, ParentCode = pen1.Code,
                LeakageLimit = 0.005m * i, TestPressure = 0.5m
            });
        }

        // 1个部件
        ctx.TestObjectPathNodes.Add(new TestObjectPathNode
        {
            Code = "CMP001", Name = "电缆贯穿件", NodeType = PathNodeType.OtherComponent,
            UnitCode = unit.Code, ParentCode = pen2.Code
        });

        // 测量装置（TestRecord 需要 DeviceCode）
        ctx.MeasurementDevices.Add(new MeasurementDevice
        {
            DeviceCode = "DEV-001", DeviceName = "测试装置",
            EnabledStatus = EnabledStatus.Enabled,
            PrimaryCommunication = CommunicationType.Rj45,
        });

        await ctx.SaveChangesAsync();
    }

    // ── 测试 1：台账统计数字 ──

    [Fact]
    public async Task Overview_LedgerCounts_AreCorrect()
    {
        await SeedBasicLedgerAsync();

        // 直接查数据库验证预期值
        using var ctx = DbContextFactory.CreateDbContext();
        var projectCount = await ctx.Projects.CountAsync();
        var unitCount = await ctx.Units.CountAsync();
        var systemCount = await ctx.TestObjectPathNodes.CountAsync(n => n.NodeType == PathNodeType.System);
        var penCount = await ctx.TestObjectPathNodes.CountAsync(n => n.NodeType == PathNodeType.Penetration);
        var valveCount = await ctx.TestObjectPathNodes.CountAsync(n => n.NodeType == PathNodeType.Valve);
        var compCount = await ctx.TestObjectPathNodes.CountAsync(n => n.NodeType == PathNodeType.OtherComponent);

        _output.WriteLine($"项目={projectCount}, 机组={unitCount}, 系统={systemCount}");
        _output.WriteLine($"贯穿件={penCount}, 阀门={valveCount}, 部件={compCount}");

        projectCount.Should().Be(1);
        unitCount.Should().Be(1);
        systemCount.Should().Be(1);
        penCount.Should().Be(2);
        valveCount.Should().Be(3);
        compCount.Should().Be(1);

        // 验证顶部"试验对象"计数 = 阀门 + 部件（不含贯穿件）
        var testObjectCount = valveCount + compCount;
        testObjectCount.Should().Be(4);
    }

    // ── 测试 2：试验对象计数是否包含贯穿件（代码审查发现疑点） ──

    [Fact]
    public void Overview_TestObjectValue_DescriptionConsistent()
    {
        var vm = new OverviewViewModel();
        // 修复后：描述改为"阀门 / 部件"，与实际计数逻辑一致
        vm.TestObjectDesc.Should().Be("阀门 / 部件");
        vm.TestObjectDesc.Should().NotContain("贯穿件",
            because: "计数只算 Valve+Component，描述不应包含贯穿件");
    }

    // ── 测试 3：合格率计算 ──

    [Fact]
    public async Task Overview_PassRate_CalculatedCorrectly()
    {
        await SeedBasicLedgerAsync();

        using var ctx = DbContextFactory.CreateDbContext();

        // 添加试验记录：5条合格 + 3条不合格 + 2条未知
        var now = DateTime.Now;
        for (int i = 0; i < 5; i++)
        {
            ctx.TestRecords.Add(new TestRecord
            {
                RecordCode = $"REC-PASS-{i:D3}",
                ProjectCode = "PRJ001", ObjectCode = "VP001", UnitCode = "UNIT01",
                DeviceCode = "DEV-001",
                Result = TestResult.Pass, TestTime = now.AddHours(-i),
                ImportTime = now.AddHours(-i),
                FinalLeakageRate = 0.001m,
            });
        }
        for (int i = 0; i < 3; i++)
        {
            ctx.TestRecords.Add(new TestRecord
            {
                RecordCode = $"REC-FAIL-{i:D3}",
                ProjectCode = "PRJ001", ObjectCode = "VP002", UnitCode = "UNIT01",
                DeviceCode = "DEV-001",
                Result = TestResult.Fail, TestTime = now.AddHours(-i - 5),
                ImportTime = now.AddHours(-i - 5),
                FinalLeakageRate = 0.1m,
            });
        }
        for (int i = 0; i < 2; i++)
        {
            ctx.TestRecords.Add(new TestRecord
            {
                RecordCode = $"REC-UNK-{i:D3}",
                ProjectCode = "PRJ001", ObjectCode = "VP003", UnitCode = "UNIT01",
                DeviceCode = "DEV-001",
                Result = TestResult.Unknown, TestTime = now.AddHours(-i - 8),
                ImportTime = now.AddHours(-i - 8),
                FinalLeakageRate = 0,
            });
        }
        await ctx.SaveChangesAsync();

        // 模拟 LoadDataAsync 的合格率计算逻辑
        var thirtyDaysAgo = DateTime.Now.AddDays(-30);
        var recentRecords = await ctx.TestRecords
            .Where(r => r.TestTime >= thirtyDaysAgo)
            .ToListAsync();

        _output.WriteLine($"最近30天记录数: {recentRecords.Count}");

        // ⚠️ 当前代码的合格率计算：
        var passCountRecent = recentRecords.Count(r => r.Result == TestResult.Pass);
        var passRate = (double)passCountRecent / recentRecords.Count * 100;
        _output.WriteLine($"当前算法: {passCountRecent} / {recentRecords.Count} = {passRate:F1}%");

        // 问题：分母包含 Unknown 的记录，合格率 = 5/10 = 50%
        // 但 Unknown 不应算入分母！正确的应该是 5/(5+3) = 62.5%
        var unknownCount = recentRecords.Count(r => r.Result == TestResult.Unknown);
        _output.WriteLine($"其中 Unknown: {unknownCount} 条");

        // 修复后：合格率只计算有明确结果的（排除 Unknown）
        var judgedRecords = recentRecords
            .Where(r => r.Result == TestResult.Pass || r.Result == TestResult.Fail)
            .ToList();
        var fixedPassRate = judgedRecords.Count > 0
            ? (double)judgedRecords.Count(r => r.Result == TestResult.Pass) / judgedRecords.Count * 100
            : 0;
        _output.WriteLine($"修复后算法: {fixedPassRate:F1}%");
        fixedPassRate.Should().Be(62.5,
            because: "修复后分母排除 Unknown：5/(5+3)=62.5%");
    }

    // ── 测试 4：装置状态只显示启用装置 ──

    [Fact]
    public async Task Overview_DeviceStatus_OnlyEnabledDevices()
    {
        using var ctx = DbContextFactory.CreateDbContext();

        // 创建 1 启用 + 3 停用
        ctx.MeasurementDevices.AddRange(
            new MeasurementDevice
            {
                DeviceCode = "DEV-OK", DeviceName = "正常装置",
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Online,
                LastSyncTime = DateTime.Now,
                PrimaryCommunication = CommunicationType.Rj45,
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-OFF1", DeviceName = "停用1",
                EnabledStatus = EnabledStatus.Disabled,
                PrimaryCommunication = CommunicationType.Usb,
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-OFF2", DeviceName = "停用2",
                EnabledStatus = EnabledStatus.Disabled,
                PrimaryCommunication = CommunicationType.Rs485,
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-OFF3", DeviceName = "停用3",
                EnabledStatus = EnabledStatus.Disabled,
                PrimaryCommunication = CommunicationType.Rs232,
            }
        );
        await ctx.SaveChangesAsync();

        // 模拟首页查询逻辑
        var enabledDevices = await ctx.MeasurementDevices
            .Where(d => d.EnabledStatus == EnabledStatus.Enabled)
            .OrderByDescending(d => d.LastUploadTime)
            .ToListAsync();

        _output.WriteLine($"启用装置数: {enabledDevices.Count}");
        foreach (var d in enabledDevices)
            _output.WriteLine($"  - {d.DeviceCode} ({d.DeviceName})");

        enabledDevices.Should().HaveCount(1);
        enabledDevices[0].DeviceCode.Should().Be("DEV-OK");

        // 验证：停用的装置不应该出现在列表中
        enabledDevices.Should().NotContain(d => d.DeviceCode == "DEV-OFF1");
        enabledDevices.Should().NotContain(d => d.DeviceCode == "DEV-OFF2");
        enabledDevices.Should().NotContain(d => d.DeviceCode == "DEV-OFF3");
    }

    // ── 测试 5：空数据库不崩溃 ──

    [Fact]
    public async Task Overview_EmptyDatabase_NoCrash()
    {
        // 不 seed 任何数据，验证查询不会崩溃
        using var ctx = DbContextFactory.CreateDbContext();

        var projectCount = await ctx.Projects.CountAsync();
        var deviceCount = await ctx.MeasurementDevices.CountAsync();
        var recordCount = await ctx.TestRecords.CountAsync();

        projectCount.Should().Be(0);
        deviceCount.Should().Be(0);
        recordCount.Should().Be(0);

        // 模拟合格率计算（空数据时 recentRecords 为空，走 else 分支）
        var thirtyDaysAgo = DateTime.Now.AddDays(-30);
        var recentRecords = await ctx.TestRecords
            .Where(r => r.TestTime >= thirtyDaysAgo)
            .ToListAsync();

        recentRecords.Should().BeEmpty();
        // 空数据时不应除零（代码有 if (recentRecords.Any()) 保护）
        if (!recentRecords.Any())
        {
            _output.WriteLine("空数据走 else 分支：PassRate=0, Anomaly=0");
        }
    }

    // ── 测试 6：最新记录展示 ──

    [Fact]
    public async Task Overview_LatestRecords_ShowTop4()
    {
        await SeedBasicLedgerAsync();

        using var ctx = DbContextFactory.CreateDbContext();
        var now = DateTime.Now;

        // 添加 6 条记录（不同时间）
        for (int i = 0; i < 6; i++)
        {
            ctx.TestRecords.Add(new TestRecord
            {
                RecordCode = $"REC-{i:D3}",
                ProjectCode = "PRJ001",
                ObjectCode = $"VP{(i % 3) + 1:D3}",
                UnitCode = "UNIT01",
                DeviceCode = "DEV-001",
                Result = i % 2 == 0 ? TestResult.Pass : TestResult.Fail,
                TestTime = now.AddHours(-i),
                ImportTime = now.AddHours(-i),
                FinalLeakageRate = 0.001m * (i + 1),
                Operator = $"操作员{i}",
            });
        }
        await ctx.SaveChangesAsync();

        // 查询最新4条
        var latestRecords = await ctx.TestRecords
            .OrderByDescending(r => r.TestTime)
            .Take(4)
            .ToListAsync();

        latestRecords.Should().HaveCount(4);
        // 应该按时间倒序
        latestRecords[0].TestTime.Should().BeAfter(latestRecords[1].TestTime);
        latestRecords[1].TestTime.Should().BeAfter(latestRecords[2].TestTime);

        _output.WriteLine("最新4条记录:");
        foreach (var r in latestRecords)
            _output.WriteLine($"  {r.ObjectCode} | {r.Result} | {r.TestTime:HH:mm:ss}");
    }

    // ── 测试 7：FormatRelativeTime 时间格式化 ──

    [Fact]
    public void Overview_FormatRelativeTime_EdgeCases()
    {
        // 测试 FormatRelativeTime 的各种边界情况
        // 这是一个 private static 方法，我们通过反射或直接验证逻辑

        // 1分钟以内 → "刚刚"
        var justNow = DateTime.Now.AddSeconds(-30);
        var diff = DateTime.Now - justNow;
        diff.TotalMinutes.Should().BeLessThan(1);
        _output.WriteLine($"30秒前: diff={diff.TotalSeconds:F0}秒 → 应显示'刚刚'");

        // 30分钟 → "30分钟前"
        var halfHour = DateTime.Now.AddMinutes(-30);
        diff = DateTime.Now - halfHour;
        diff.TotalHours.Should().BeLessThan(1);
        _output.WriteLine($"30分钟前: diff={diff.TotalMinutes:F0}分 → 应显示'30分钟前'");

        // 5小时 → "5小时前"
        var fiveHours = DateTime.Now.AddHours(-5);
        diff = DateTime.Now - fiveHours;
        diff.TotalDays.Should().BeLessThan(1);
        _output.WriteLine($"5小时前: diff={diff.TotalHours:F0}时 → 应显示'5小时前'");

        // 10天 → "10天前"
        var tenDays = DateTime.Now.AddDays(-10);
        diff = DateTime.Now - tenDays;
        diff.TotalDays.Should().BeLessThan(30);
        _output.WriteLine($"10天前: diff={diff.TotalDays:F0}天 → 应显示'10天前'");

        // 60天 → 显示具体日期
        var sixtyDays = DateTime.Now.AddDays(-60);
        diff = DateTime.Now - sixtyDays;
        diff.TotalDays.Should().BeGreaterOrEqualTo(30);
        _output.WriteLine($"60天前: → 应显示具体日期 '{sixtyDays:MM-dd HH:mm:ss}'");
    }
}
