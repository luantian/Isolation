using ClosedXML.Excel;
using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// 按文档导入 xlsx（甲方《实验记录表格式》模板）解析测试：
/// 表头探测（跳过标题行）、合并单元格向下填充（系统/贯穿件编号）、
/// 单位换算（限值 Ncm³/h ÷60、试验压力 KPa ÷1000）、
/// 贯穿件编号拼入阀门显示名、日期（单元格/文本/序列号）解析。
/// 用 ClosedXML 编程生成仿真实模板的文件（含合并单元格与文本型数字）。
/// </summary>
public sealed class DeviceRecordXlsxImportTests : IDisposable
{
    public DeviceRecordXlsxImportTests()
    {
        TestDbContextHelper.EnsureEncodingsRegistered();
        TestDbContextHelper.SetupTestFactory($"XlsxImport_{Guid.NewGuid():N}");
    }

    public void Dispose() => TestDbContextHelper.ResetTestFactory();

    private static DataUploadService CreateService()
        => new(new TestRecordService(DbContextFactory.CreateDbContext()));

    private static string CreateSampleXlsx()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("记录表");

        // 行1：标题（须被表头探测跳过）
        ws.Cell(1, 1).Value = "海南3机组  安全壳隔离阀密封性试验记录";

        // 行2：表头（与甲方模板一致的 16 列；B 列表头为空=贯穿件编号）
        string[] headers =
        [
            "系统", "", "贯穿件直径", "试验阀门", "RCC-M或可靠性等级", "阀门公称直径（mm）",
            "阀门泄漏率设计最大值（Ncm³/h）", "试验方法", "试验压力(KPa)", "试验介质",
            "试验仪器读数", "试验仪器读数单位", "泄漏率值（气）（Nml/h）", "试验仪器编号", "试验日期", "备注",
        ];
        for (int c = 0; c < headers.Length; c++)
            ws.Cell(2, c + 1).Value = headers[c];

        // 数据行（对齐模板合并结构：系统/贯穿件直径/贯穿件编号两行一合并）
        // 行3-4：CAM / PN217；行5-6：CAM / PN218
        ws.Cell(3, 1).Value = "CAM";
        ws.Cell(3, 2).Value = "PN217";
        ws.Cell(3, 3).Value = 350;
        ws.Cell(3, 4).Value = "3CAM003VA";
        ws.Cell(3, 7).Value = 6895;
        ws.Cell(3, 9).Value = 423;
        ws.Cell(3, 11).Value = "110.15";       // 文本型读数（模板实际为字符串）
        ws.Cell(3, 14).Value = "B-GLM-A1-002";
        ws.Cell(3, 15).Value = new DateTime(2025, 7, 9);  // 日期单元格
        ws.Cell(3, 16).Value = "流量补充法";

        ws.Cell(4, 4).Value = "3CAM005VA";
        ws.Cell(4, 7).Value = 6895;
        ws.Cell(4, 9).Value = 425;
        ws.Cell(4, 11).Value = "103.23";
        ws.Cell(4, 14).Value = "B-GLM-A1-002";
        ws.Cell(4, 15).Value = new DateTime(2025, 7, 9);

        ws.Cell(5, 1).Value = "CAM";
        ws.Cell(5, 2).Value = "PN218";
        ws.Cell(5, 4).Value = "3CAM004VA";
        ws.Cell(5, 7).Value = 6895;
        ws.Cell(5, 9).Value = 430;
        ws.Cell(5, 11).Value = 99.95;          // 数值型读数
        ws.Cell(5, 14).Value = "B-GLM-A1-002";
        ws.Cell(5, 15).Value = "45848";        // 文本型 Excel 日期序列号

        // 合并单元格（模板实际结构）
        ws.Range(3, 1, 4, 1).Merge();
        ws.Range(3, 2, 4, 2).Merge();
        ws.Range(3, 3, 4, 3).Merge();

        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public async Task ParseXlsx_DetectsHeader_MergesCells_AndConvertsUnits()
    {
        var path = CreateSampleXlsx();
        try
        {
            var rows = await CreateService().ParseMultiRowRecordsXlsxAsync(path);

            rows.Should().HaveCount(3, "3 行有效试验阀门记录");

            // 第1行：单位换算 + 文本型读数 + 日期单元格
            var r1 = rows[0];
            r1.ObjectCode.Should().Be("3CAM003VA");
            r1.SystemName.Should().Be("CAM");
            r1.UnitName.Should().Be("海南3机组", "应从标题行提取机组名供导入端自动归属");
            r1.ValveDisplayName.Should().Be("3CAM003VA(PN217)", "贯穿件编号应拼入阀门显示名");
            r1.LeakageLimit.Should().BeApproximately(6895m / 60m, 0.0001m,
                "限值 Ncm³/h 应 ÷60 换算为 Nml/min（1 Ncm³=1 Nml）");
            r1.TestPressure.Should().Be(0.423m, "试验压力 KPa 应 ÷1000 换算为库存 MPa");
            r1.LeakageRate.Should().Be(110.15m, "文本型读数应可解析");
            r1.DeviceCode.Should().Be("B-GLM-A1-002", "试验仪器编号应识别为测量装置");
            r1.TestTime.Should().Be(new DateTime(2025, 7, 9));

            // 第2行：合并单元格续行——系统/贯穿件编号继承上一行
            var r2 = rows[1];
            r2.ObjectCode.Should().Be("3CAM005VA");
            r2.SystemName.Should().Be("CAM", "系统列合并单元格应向下填充");
            r2.ValveDisplayName.Should().Be("3CAM005VA(PN217)", "贯穿件编号合并单元格应向下填充");
            r2.TestPressure.Should().Be(0.425m);

            // 第3行：数值型读数 + 文本型 Excel 日期序列号（45848 = 2025-07-10）
            var r3 = rows[2];
            r3.LeakageRate.Should().Be(99.95m);
            r3.ValveDisplayName.Should().Be("3CAM004VA(PN218)");
            r3.TestTime.Should().Be(new DateTime(2025, 7, 10), "文本型日期序列号应可解析");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseXlsx_RealCustomerTemplate_WhenPresent()
    {
        // 从测试程序集目录向上找仓库内的真实客户模板（不存在则跳过，兼容独立环境）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "doc", "实验记录表格式(1).xlsx")))
            dir = dir.Parent!;
        var path = dir == null ? null : Path.Combine(dir.FullName, "doc", "实验记录表格式(1).xlsx");
        if (!File.Exists(path)) return;

        var rows = await CreateService().ParseMultiRowRecordsXlsxAsync(path);

        rows.Should().HaveCount(6, "客户模板含 6 行试验记录");
        rows.Should().OnlyContain(r => r.ObjectCode.StartsWith("3CAM", StringComparison.Ordinal));

        var r1 = rows[0];
        r1.SystemName.Should().Be("CAM");
        r1.UnitName.Should().Be("海南3机组", "真实模板标题同样应提取到机组名");
        r1.ValveDisplayName.Should().Be("3CAM003VA(PN217)");
        r1.LeakageLimit.Should().BeApproximately(6895m / 60m, 0.0001m);
        r1.TestPressure.Should().Be(0.423m);
        r1.LeakageRate.Should().Be(110.15m);
        r1.DeviceCode.Should().Be("B-GLM-A1-002");

        // 合并单元格续行：第 2 行（3CAM005VA）系统/贯穿件编号继承自上一行
        rows[1].SystemName.Should().Be("CAM");
        rows[1].ValveDisplayName.Should().Be("3CAM005VA(PN217)");

        // 最后一段（PN219 两行合并）
        rows[^1].ValveDisplayName.Should().Be("3CAM009VA(PN219)");
    }

    [Fact]
    public async Task ParseXlsx_RejectsFileWithoutHeaderColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("空表");
                ws.Cell(1, 1).Value = "随便一个没有表头的表";
                ws.Cell(2, 1).Value = "数据";
                wb.SaveAs(path);
            }

            var act = () => CreateService().ParseMultiRowRecordsXlsxAsync(path);
            await act.Should().ThrowAsync<FormatException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnsurePath_CreatesValveWithPenetrationDisplayName()
    {
        var path = CreateSampleXlsx();
        try
        {
            var rows = await CreateService().ParseMultiRowRecordsXlsxAsync(path);

            var valveCode = await CreateService().EnsureCsvPathExistsAsync(
                "UNIT-01", rows[0].SystemName, rows[0].ObjectCode!,
                rows[0].LeakageLimit, rows[0].TestPressure, rows[0].ValveDisplayName);

            valveCode.Should().Be("3CAM003VA");
            using var context = DbContextFactory.CreateDbContext();
            var valve = await context.TestObjectPathNodes.SingleAsync(n => n.Code == "3CAM003VA");
            valve.Name.Should().Be("3CAM003VA(PN217)", "阀门节点显示名应带贯穿件编号");
            valve.LeakageLimit.Should().BeApproximately(6895m / 60m, 0.0001m, "节点限值应为换算后的 Nml/min");
            valve.TestPressure.Should().Be(0.423m, "节点试验压力应为换算后的 MPa");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
