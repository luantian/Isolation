using System.Text;
using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// 按文档导入相关测试：
/// - 实验报表 CSV 的"系统"列解析（SystemName）
/// - GBK 编码文件读取
/// - EnsureCsvPathExistsAsync 的"系统→阀门"建链（创建/幂等/同名合并/跨机组冲突）
/// </summary>
public sealed class MultiRowCsvSystemColumnTests : IDisposable
{
    public MultiRowCsvSystemColumnTests()
    {
        TestDbContextHelper.EnsureEncodingsRegistered();
        TestDbContextHelper.SetupTestFactory($"DocImport_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        TestDbContextHelper.ResetTestFactory();
    }

    private static DataUploadService CreateService()
    {
        var context = DbContextFactory.CreateDbContext();
        return new DataUploadService(new TestRecordService(context));
    }

    private const string SampleCsv =
        "序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2,试验压力P2,试验压力P1,试验仪器读数,实验日期,实验结果\r\n" +
        "1,冷却水系统,DN50,V-001,DN25,0.05,0.321,0.344,0.344,0.021,2026/07/12 10:00:00,合格\r\n" +
        "2,冷却水系统,DN50,V-002,DN25,0.05,0.321,0.344,0.344,0.022,2026/07/12 10:05:00,合格\r\n" +
        "3,蒸汽系统,DN80,V-101,DN50,0.08,0.321,0.344,0.344,0.031,2026/07/12 11:00:00,不合格\r\n";

    [Fact]
    public void ParseMultiRowRecordsCsv_SetsSystemNameFromSystemColumn()
    {
        var service = CreateService();

        var packages = service.ParseMultiRowRecordsCsv(SampleCsv);

        packages.Should().HaveCount(3);
        packages[0].SystemName.Should().Be("冷却水系统");
        packages[1].SystemName.Should().Be("冷却水系统");
        packages[2].SystemName.Should().Be("蒸汽系统");
        packages[0].ObjectCode.Should().Be("V-001");
        packages[0].LeakageLimit.Should().Be(0.05m);
        packages[0].TestPressure.Should().Be(0.344m);
    }

    [Fact]
    public async Task ParseMultiRowRecordsCsvFromFileAsync_ReadsGbkEncodedFile()
    {
        TestDbContextHelper.EnsureEncodingsRegistered();
        var path = Path.Combine(Path.GetTempPath(), $"doc_{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.GetEncoding(936).GetBytes(SampleCsv));

            var service = CreateService();
            var packages = await service.ParseMultiRowRecordsCsvFromFileAsync(path);

            packages.Should().HaveCount(3);
            packages[2].SystemName.Should().Be("蒸汽系统");
            packages[2].ObjectCode.Should().Be("V-101");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseMultiRowRecordsCsvFromFileAsync_RejectsNonDocumentCsv()
    {
        var path = Path.Combine(Path.GetTempPath(), $"curve_{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "导出时间,实时压力P1,瞬时流量M1,温度T_R\r\n0,0.1,1.2,25\r\n");

            var service = CreateService();
            var act = () => service.ParseMultiRowRecordsCsvFromFileAsync(path);

            await act.Should().ThrowAsync<FormatException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnsureCsvPathExistsAsync_CreatesSystemAndValveNodes()
    {
        var service = CreateService();

        var valveCode = await service.EnsureCsvPathExistsAsync("UNIT-01", "冷却水系统", "V-001", 0.05m, 0.344m);

        valveCode.Should().Be("V-001");
        using (var context = DbContextFactory.CreateDbContext())
        {
            var system = await context.TestObjectPathNodes.SingleAsync(n => n.NodeType == PathNodeType.System);
            system.UnitCode.Should().Be("UNIT-01");
            system.Name.Should().Be("冷却水系统");
            system.ParentCode.Should().BeNull();

            var valve = await context.TestObjectPathNodes.SingleAsync(n => n.Code == "V-001");
            valve.NodeType.Should().Be(PathNodeType.Valve);
            valve.UnitCode.Should().Be("UNIT-01");
            valve.ParentCode.Should().Be(system.Code);
            valve.LeakageLimit.Should().Be(0.05m);
            valve.TestPressure.Should().Be(0.344m);
        }
    }

    [Fact]
    public async Task EnsureCsvPathExistsAsync_MergesSameSystemAndIsIdempotent()
    {
        var service = CreateService();

        await service.EnsureCsvPathExistsAsync("UNIT-01", "冷却水系统", "V-001", 0.05m, 0.344m);
        await service.EnsureCsvPathExistsAsync("UNIT-01", "冷却水系统", "V-002", 0.05m, 0.344m);
        // 同一阀门再次导入：复用，不重复创建
        await service.EnsureCsvPathExistsAsync("UNIT-01", "冷却水系统", "V-001", 0.05m, 0.344m);

        using var context = DbContextFactory.CreateDbContext();
        var systems = await context.TestObjectPathNodes.Where(n => n.NodeType == PathNodeType.System).ToListAsync();
        systems.Should().HaveCount(1);

        var valves = await context.TestObjectPathNodes.Where(n => n.NodeType == PathNodeType.Valve).ToListAsync();
        valves.Should().HaveCount(2);
        valves.All(v => v.ParentCode == systems[0].Code).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureCsvPathExistsAsync_RejectsValveBelongingToOtherUnit()
    {
        using (var context = DbContextFactory.CreateDbContext())
        {
            context.TestObjectPathNodes.Add(new TestObjectPathNode
            {
                Code = "V-999",
                Name = "V-999",
                NodeType = PathNodeType.Valve,
                UnitCode = "UNIT-OTHER",
                ParentCode = null,
                Status = EnabledStatus.Enabled,
            });
            await context.SaveChangesAsync();
        }

        var service = CreateService();
        var act = () => service.EnsureCsvPathExistsAsync("UNIT-01", "冷却水系统", "V-999", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已存在于其他机组*");
    }

    [Fact]
    public async Task EnsureCsvPathExistsAsync_EmptySystemNameFallsBackToUncategorized()
    {
        var service = CreateService();

        await service.EnsureCsvPathExistsAsync("UNIT-01", "  ", "V-001", null, null);

        using var context = DbContextFactory.CreateDbContext();
        var system = await context.TestObjectPathNodes.SingleAsync(n => n.NodeType == PathNodeType.System);
        system.Name.Should().Be("未分类系统");
    }
}
