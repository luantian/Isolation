using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace IsolationLeakage.App.Tests.Integration;

/// <summary>
/// 试验对象页面业务测试：项目/机组 CRUD、路径节点树、级联删除
/// </summary>
[Collection("IntegrationTests")]
public class MasterDataBusinessTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalConnectionString;

    public MasterDataBusinessTests(ITestOutputHelper output)
    {
        _output = output;
        _originalConnectionString = DbContextFactory.GetDefaultConnectionString();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = @".\CITADEL",
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

        // 确保基础设备存在（TestRecord 的 DeviceCode 需要 FK 引用）
        if (!await ctx.MeasurementDevices.AnyAsync(d => d.DeviceCode == "DEV-001"))
        {
            ctx.MeasurementDevices.Add(new MeasurementDevice
            {
                DeviceCode = "DEV-001", DeviceName = "测试装置",
                EnabledStatus = EnabledStatus.Enabled,
                PrimaryCommunication = CommunicationType.Rj45,
            });
            await ctx.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        using var ctx = DbContextFactory.CreateDbContext();
        ctx.TestRecords.RemoveRange(ctx.TestRecords);
        ctx.TestObjectPathNodes.RemoveRange(ctx.TestObjectPathNodes);
        ctx.MeasurementDevices.RemoveRange(ctx.MeasurementDevices);
        ctx.Units.RemoveRange(ctx.Units);
        ctx.Projects.RemoveRange(ctx.Projects);
        await ctx.SaveChangesAsync();
    }

    public void Dispose()
    {
        DbContextFactory.Configure(_originalConnectionString);
    }

    private AppDbContext CreateContext() => DbContextFactory.CreateDbContext();

    // ═══════════════════════════════════════════════
    // 项目管理测试
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Project_Add_BasicCrud()
    {
        using var ctx = CreateContext();
        var service = new ProjectService(ctx);

        var project = await service.AddAsync("PRJ-001", "测试项目A", "备注");

        project.Code.Should().Be("PRJ-001");
        project.Name.Should().Be("测试项目A");
        project.Remark.Should().Be("备注");
        project.Status.Should().Be(EnabledStatus.Enabled);
    }

    [Fact]
    public async Task Project_Add_DuplicateCode_Throws()
    {
        using var ctx = CreateContext();
        var service = new ProjectService(ctx);

        await service.AddAsync("PRJ-DUP", "项目X", null);

        var act = async () => await service.AddAsync("PRJ-DUP", "项目Y", null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已存在*");
    }

    [Fact]
    public async Task Project_Add_DuplicateName_Throws()
    {
        using var ctx = CreateContext();
        var service = new ProjectService(ctx);

        await service.AddAsync("PRJ-A", "相同名称", null);

        var act = async () => await service.AddAsync("PRJ-B", "相同名称", null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已存在*");
    }

    [Fact]
    public async Task Project_Delete_CascadesToUnitsAndNodesAndRecords()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        // 创建：项目 → 机组 → 系统 → 阀门
        await projectSvc.AddAsync("PRJ-CASC", "级联测试项目", null);
        await unitSvc.AddAsync("PRJ-CASC", "UNIT-C1", "级联机组", null);

        var system = new TestObjectPathNode
        {
            Code = "SYS-C1", Name = "系统C", NodeType = PathNodeType.System, UnitCode = "UNIT-C1"
        };
        await pathSvc.AddAsync(system);

        var valve = new TestObjectPathNode
        {
            Code = "VP-C1", Name = "阀门C", NodeType = PathNodeType.Valve,
            UnitCode = "UNIT-C1", ParentCode = "SYS-C1"
        };
        await pathSvc.AddAsync(valve);

        // 添加试验记录
        ctx.TestRecords.Add(new TestRecord
        {
            RecordCode = "REC-CASC-1", ProjectCode = "PRJ-CASC",
            ObjectCode = "VP-C1", UnitCode = "UNIT-C1", DeviceCode = "DEV-001",
            Result = TestResult.Pass, TestTime = DateTime.Now,
            ImportTime = DateTime.Now, FinalLeakageRate = 0.001m,
        });
        await ctx.SaveChangesAsync();

        // 删除项目
        var result = await projectSvc.DeleteAsync("PRJ-CASC");
        result.Should().BeTrue();

        // 验证级联删除
        (await ctx.Projects.CountAsync(p => p.Code == "PRJ-CASC")).Should().Be(0);
        (await ctx.Units.CountAsync(u => u.Code == "UNIT-C1")).Should().Be(0);
        (await ctx.TestObjectPathNodes.CountAsync(n => n.Code == "SYS-C1" || n.Code == "VP-C1")).Should().Be(0);
        (await ctx.TestRecords.CountAsync(r => r.RecordCode == "REC-CASC-1")).Should().Be(0);

        _output.WriteLine("✅ 项目删除级联清除：机组/路径节点/试验记录 全部删除");
    }

    [Fact]
    public async Task Project_ToggleStatus_Works()
    {
        using var ctx = CreateContext();
        var service = new ProjectService(ctx);
        await service.AddAsync("PRJ-TOG", "启停测试", null);

        await service.SetStatusAsync("PRJ-TOG", EnabledStatus.Disabled);
        var disabled = await ctx.Projects.FirstAsync(p => p.Code == "PRJ-TOG");
        disabled.Status.Should().Be(EnabledStatus.Disabled);

        await service.SetStatusAsync("PRJ-TOG", EnabledStatus.Enabled);
        var enabled = await ctx.Projects.FirstAsync(p => p.Code == "PRJ-TOG");
        enabled.Status.Should().Be(EnabledStatus.Enabled);
    }

    // ═══════════════════════════════════════════════
    // 机组管理测试
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Unit_Add_BasicCrud()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);

        await projectSvc.AddAsync("PRJ-U", "项目U", null);
        var unit = await unitSvc.AddAsync("PRJ-U", "UNIT-001", "1号机组", "测试备注");

        unit.ProjectCode.Should().Be("PRJ-U");
        unit.Code.Should().Be("UNIT-001");
        unit.Name.Should().Be("1号机组");
    }

    [Fact]
    public async Task Unit_Add_DuplicateInSameProject_Throws()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);

        await projectSvc.AddAsync("PRJ-UD", "项目UD", null);
        await unitSvc.AddAsync("PRJ-UD", "UNIT-D1", "机组1", null);

        var act = async () => await unitSvc.AddAsync("PRJ-UD", "UNIT-D1", "机组2", null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Unit_CodeIsPrimaryKey_SameCodeInDifferentProject_NotAllowed()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);

        await projectSvc.AddAsync("PRJ-X2", "项目X", null);
        await projectSvc.AddAsync("PRJ-Y2", "项目Y", null);

        await unitSvc.AddAsync("PRJ-X2", "UNIT-UNQ", "机组唯一X", null);

        // Unit 的 Code 是主键，全局唯一，不同项目也不能重复
        var act = async () => await unitSvc.AddAsync("PRJ-Y2", "UNIT-UNQ", "机组唯一Y", null);
        await act.Should().ThrowAsync<DbUpdateException>();

        _output.WriteLine("✅ Unit.Code 是主键，全局唯一，不同项目也不能重复");
    }

    [Fact]
    public async Task Unit_Delete_CascadesToNodesAndRecords()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-UDC", "级联机组测试", null);
        await unitSvc.AddAsync("PRJ-UDC", "UNIT-DC1", "待删机组", null);

        var valve = new TestObjectPathNode
        {
            Code = "VP-DC1", Name = "阀门DC", NodeType = PathNodeType.Valve,
            UnitCode = "UNIT-DC1"
        };
        await pathSvc.AddAsync(valve);

        ctx.TestRecords.Add(new TestRecord
        {
            RecordCode = "REC-UDC-1", ProjectCode = "PRJ-UDC",
            ObjectCode = "VP-DC1", UnitCode = "UNIT-DC1", DeviceCode = "DEV-001",
            Result = TestResult.Pass, TestTime = DateTime.Now,
            ImportTime = DateTime.Now, FinalLeakageRate = 0.001m,
        });
        await ctx.SaveChangesAsync();

        var result = await unitSvc.DeleteAsync("UNIT-DC1");
        result.Should().BeTrue();

        (await ctx.Units.CountAsync(u => u.Code == "UNIT-DC1")).Should().Be(0);
        (await ctx.TestObjectPathNodes.CountAsync(n => n.Code == "VP-DC1")).Should().Be(0);
        (await ctx.TestRecords.CountAsync(r => r.RecordCode == "REC-UDC-1")).Should().Be(0);

        _output.WriteLine("✅ 机组删除级联清除：路径节点/试验记录");
    }

    // ═══════════════════════════════════════════════
    // 路径节点树测试
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task PathNode_Add_ValidHierarchy()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-T", "树测试", null);
        await unitSvc.AddAsync("PRJ-T", "UNIT-T", "树机组", null);

        // 系统 → 贯穿件 → 阀门（合法层级）
        var sys = new TestObjectPathNode
        {
            Code = "SYS-T", Name = "系统", NodeType = PathNodeType.System, UnitCode = "UNIT-T"
        };
        await pathSvc.AddAsync(sys);

        var pen = new TestObjectPathNode
        {
            Code = "PEN-T", Name = "贯穿件", NodeType = PathNodeType.Penetration,
            UnitCode = "UNIT-T", ParentCode = "SYS-T"
        };
        await pathSvc.AddAsync(pen);

        var valve = new TestObjectPathNode
        {
            Code = "VP-T", Name = "阀门", NodeType = PathNodeType.Valve,
            UnitCode = "UNIT-T", ParentCode = "PEN-T"
        };
        await pathSvc.AddAsync(valve);

        _output.WriteLine("✅ 合法层级：系统→贯穿件→阀门 创建成功");
    }

    [Fact]
    public async Task PathNode_Add_InvalidHierarchy_Throws()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-TH", "非法层级测试", null);
        await unitSvc.AddAsync("PRJ-TH", "UNIT-TH", "层级机组", null);

        var valve = new TestObjectPathNode
        {
            Code = "VP-BAD", Name = "阀门", NodeType = PathNodeType.Valve, UnitCode = "UNIT-TH"
        };
        await pathSvc.AddAsync(valve);

        // 阀门下不能有子节点
        var badChild = new TestObjectPathNode
        {
            Code = "PEN-BAD", Name = "贯穿件", NodeType = PathNodeType.Penetration,
            UnitCode = "UNIT-TH", ParentCode = "VP-BAD"
        };

        var act = async () => await pathSvc.AddAsync(badChild);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*不能在*阀门*下创建*贯穿件*");

        _output.WriteLine("✅ 阀门下不能创建子节点：正确拒绝");
    }

    [Fact]
    public async Task PathNode_Add_DuplicateCode_Throws()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-DC", "编号重复测试", null);
        await unitSvc.AddAsync("PRJ-DC", "UNIT-DC", "机组", null);

        var n1 = new TestObjectPathNode
        {
            Code = "NODE-DUP", Name = "节点1", NodeType = PathNodeType.System, UnitCode = "UNIT-DC"
        };
        await pathSvc.AddAsync(n1);

        var n2 = new TestObjectPathNode
        {
            Code = "NODE-DUP", Name = "节点2", NodeType = PathNodeType.System, UnitCode = "UNIT-DC"
        };
        var act = async () => await pathSvc.AddAsync(n2);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*编号已存在*");
    }

    [Fact]
    public async Task PathNode_Delete_WithChildren_Throws()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-DD", "删除保护测试", null);
        await unitSvc.AddAsync("PRJ-DD", "UNIT-DD", "机组", null);

        var parent = new TestObjectPathNode
        {
            Code = "SYS-DD", Name = "系统", NodeType = PathNodeType.System, UnitCode = "UNIT-DD"
        };
        await pathSvc.AddAsync(parent);

        var child = new TestObjectPathNode
        {
            Code = "VP-DD", Name = "阀门", NodeType = PathNodeType.Valve,
            UnitCode = "UNIT-DD", ParentCode = "SYS-DD"
        };
        await pathSvc.AddAsync(child);

        var act = async () => await pathSvc.DeleteAsync("SYS-DD");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*子节点*");

        _output.WriteLine("✅ 有子节点时拒绝删除");
    }

    [Fact]
    public async Task PathNode_Delete_WithTestRecords_Throws()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-DR", "记录保护测试", null);
        await unitSvc.AddAsync("PRJ-DR", "UNIT-DR", "机组", null);

        var valve = new TestObjectPathNode
        {
            Code = "VP-DR", Name = "阀门", NodeType = PathNodeType.Valve, UnitCode = "UNIT-DR"
        };
        await pathSvc.AddAsync(valve);

        ctx.TestRecords.Add(new TestRecord
        {
            RecordCode = "REC-DR-1", ProjectCode = "PRJ-DR",
            ObjectCode = "VP-DR", UnitCode = "UNIT-DR", DeviceCode = "DEV-001",
            Result = TestResult.Pass, TestTime = DateTime.Now,
            ImportTime = DateTime.Now, FinalLeakageRate = 0.001m,
        });
        await ctx.SaveChangesAsync();

        var act = async () => await pathSvc.DeleteAsync("VP-DR");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*历史试验数据*");

        _output.WriteLine("✅ 有试验记录时拒绝删除");
    }

    [Fact]
    public async Task PathNode_GetTree_BuildsCorrectStructure()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-TR", "树构建测试", null);
        await unitSvc.AddAsync("PRJ-TR", "UNIT-TR", "机组", null);

        var sys = new TestObjectPathNode
        {
            Code = "SYS-TR", Name = "系统", NodeType = PathNodeType.System, UnitCode = "UNIT-TR"
        };
        await pathSvc.AddAsync(sys);

        // 系统下 2 个贯穿件
        for (int i = 1; i <= 2; i++)
        {
            await pathSvc.AddAsync(new TestObjectPathNode
            {
                Code = $"PEN-TR{i}", Name = $"贯穿件{i}", NodeType = PathNodeType.Penetration,
                UnitCode = "UNIT-TR", ParentCode = "SYS-TR"
            });
            // 每个贯穿件下 3 个阀门
            for (int j = 1; j <= 3; j++)
            {
                await pathSvc.AddAsync(new TestObjectPathNode
                {
                    Code = $"VP-TR{i}{j}", Name = $"阀门{i}-{j}", NodeType = PathNodeType.Valve,
                    UnitCode = "UNIT-TR", ParentCode = $"PEN-TR{i}"
                });
            }
        }

        var tree = await pathSvc.GetTreeAsync("UNIT-TR");

        tree.Should().HaveCount(1, "应该有1个根节点（系统）");
        tree[0].Children.Should().HaveCount(2, "系统下2个贯穿件");
        foreach (var pen in tree[0].Children)
        {
            pen.Children.Should().HaveCount(3, "每个贯穿件下3个阀门");
        }

        _output.WriteLine("✅ 树构建正确：1系统 → 2贯穿件 → 各3阀门");
    }

    [Fact]
    public async Task PathNode_Search_FindsByCodeAndName()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-S", "搜索测试", null);
        await unitSvc.AddAsync("PRJ-S", "UNIT-S", "机组", null);

        await pathSvc.AddAsync(new TestObjectPathNode
        {
            Code = "SYS-S", Name = "RHR系统", NodeType = PathNodeType.System, UnitCode = "UNIT-S"
        });
        await pathSvc.AddAsync(new TestObjectPathNode
        {
            Code = "1RHR040VP", Name = "安全壳隔离阀", NodeType = PathNodeType.Valve, UnitCode = "UNIT-S"
        });
        await pathSvc.AddAsync(new TestObjectPathNode
        {
            Code = "1RHR050VP", Name = "排气阀", NodeType = PathNodeType.Valve, UnitCode = "UNIT-S"
        });

        // 按编号搜索
        var byCode = await pathSvc.SearchAsync("UNIT-S", "040");
        byCode.Should().HaveCount(1);
        byCode[0].Code.Should().Be("1RHR040VP");

        // 按名称搜索（中文）
        var byName = await pathSvc.SearchAsync("UNIT-S", "隔离");
        byName.Should().HaveCount(1);
        byName[0].Code.Should().Be("1RHR040VP");

        // 搜索无结果
        var noResult = await pathSvc.SearchAsync("UNIT-S", "不存在的");
        noResult.Should().BeEmpty();

        _output.WriteLine("✅ 搜索支持按编号和中文名称");
    }

    [Fact]
    public async Task PathNode_TestStatistics_CalculatesCorrectly()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);
        var pathSvc = new TestObjectPathService(ctx);

        await projectSvc.AddAsync("PRJ-ST", "统计测试", null);
        await unitSvc.AddAsync("PRJ-ST", "UNIT-ST", "机组", null);

        await pathSvc.AddAsync(new TestObjectPathNode
        {
            Code = "VP-ST", Name = "统计阀门", NodeType = PathNodeType.Valve, UnitCode = "UNIT-ST"
        });

        // 添加试验记录
        var now = DateTime.Now;
        ctx.TestRecords.AddRange(
            new TestRecord { RecordCode = "REC-ST-1", ProjectCode = "PRJ-ST", ObjectCode = "VP-ST",
                UnitCode = "UNIT-ST", DeviceCode = "DEV-001", Result = TestResult.Pass,
                TestTime = now.AddHours(-3), ImportTime = now.AddHours(-3), FinalLeakageRate = 0.001m },
            new TestRecord { RecordCode = "REC-ST-2", ProjectCode = "PRJ-ST", ObjectCode = "VP-ST",
                UnitCode = "UNIT-ST", DeviceCode = "DEV-001", Result = TestResult.Fail,
                TestTime = now.AddHours(-2), ImportTime = now.AddHours(-2), FinalLeakageRate = 0.5m },
            new TestRecord { RecordCode = "REC-ST-3", ProjectCode = "PRJ-ST", ObjectCode = "VP-ST",
                UnitCode = "UNIT-ST", DeviceCode = "DEV-001", Result = TestResult.Pass,
                TestTime = now.AddHours(-1), ImportTime = now.AddHours(-1), FinalLeakageRate = 0.002m }
        );
        await ctx.SaveChangesAsync();

        var (total, failed, lastTime) = await pathSvc.GetTestStatisticsAsync("VP-ST");

        total.Should().Be(3);
        failed.Should().Be(1);
        lastTime.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromSeconds(5));

        _output.WriteLine($"✅ 统计：共{total}次，不合格{failed}次，最近{lastTime:HH:mm:ss}");
    }

    // ═══════════════════════════════════════════════
    // 边界场景测试
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Project_Delete_NonExistent_ReturnsFalse()
    {
        using var ctx = CreateContext();
        var service = new ProjectService(ctx);

        var result = await service.DeleteAsync("NOT-EXIST");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task PathNode_Add_TrimWhitespace()
    {
        using var ctx = CreateContext();
        var projectSvc = new ProjectService(ctx);
        var unitSvc = new UnitService(ctx);

        // ProjectService.AddAsync 会 Trim
        var project = await projectSvc.AddAsync("  PRJ-TRIM  ", "  去空格项目  ", null);
        project.Code.Should().Be("PRJ-TRIM");
        project.Name.Should().Be("去空格项目");
    }
}
