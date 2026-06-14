using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 报表导出服务
/// </summary>
public sealed class ReportExportService
{
    /// <summary>
    /// 导出试验记录到Excel
    /// </summary>
    /// <param name="records">试验记录列表</param>
    /// <param name="filePath">输出文件路径</param>
    public void ExportTestRecordsToExcel(IEnumerable<TestRecord> records, string filePath)
    {
        if (records == null)
            throw new ArgumentNullException(nameof(records));
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("试验记录");

        // 定义表头
        var headers = new[]
        {
            "记录编号", "项目", "机组", "对象编码", "对象类型", "测量装置",
            "试验时间", "试验压力", "泄漏限值", "最终泄漏率", "判定结果", "操作人员"
        };

        // 写入表头
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x44, 0x72, 0xC4);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // 写入数据
        int row = 2;
        foreach (var record in records)
        {
            worksheet.Cell(row, 1).Value = record.RecordCode;
            worksheet.Cell(row, 2).Value = record.ProjectName ?? record.ProjectCode;
            worksheet.Cell(row, 3).Value = record.UnitName ?? record.UnitCode;
            worksheet.Cell(row, 4).Value = record.ObjectCode;
            worksheet.Cell(row, 5).Value = GetObjectTypeText(record.ObjectType);
            worksheet.Cell(row, 6).Value = record.Device?.DeviceName ?? record.DeviceCode;
            worksheet.Cell(row, 7).Value = record.TestTime.ToString("yyyy-MM-dd HH:mm:ss");
            worksheet.Cell(row, 8).Value = record.TestPressure;
            worksheet.Cell(row, 9).Value = record.LeakageLimit;
            worksheet.Cell(row, 10).Value = record.FinalLeakageRate;
            worksheet.Cell(row, 11).Value = GetResultText(record.Result);
            worksheet.Cell(row, 12).Value = record.Operator;
            row++;
        }

        // 自动调整列宽
        worksheet.Columns().AdjustToContents();

        // 确保目录存在
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        workbook.SaveAs(filePath);
    }

    /// <summary>
    /// 导出统计结果到Excel
    /// </summary>
    /// <param name="statsData">统计数据字典（工作表名称 -> 数据行）</param>
    /// <param name="filePath">输出文件路径</param>
    public void ExportStatisticsToExcel(Dictionary<string, List<Dictionary<string, object>>> statsData, string filePath)
    {
        if (statsData == null)
            throw new ArgumentNullException(nameof(statsData));
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        using var workbook = new XLWorkbook();

        foreach (var sheet in statsData)
        {
            var worksheet = workbook.Worksheets.Add(sheet.Key);
            var dataRows = sheet.Value;

            if (dataRows == null || dataRows.Count == 0)
                continue;

            // 获取所有列名
            var columnNames = dataRows[0].Keys.ToList();

            // 写入表头
            for (int i = 0; i < columnNames.Count; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = columnNames[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x44, 0x72, 0xC4);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // 写入数据
            int row = 2;
            foreach (var dataRow in dataRows)
            {
                int col = 1;
                foreach (var columnName in columnNames)
                {
                    var value = dataRow[columnName];
                    var cell = worksheet.Cell(row, col);

                    if (value is DateTime dt)
                    {
                        cell.Value = dt.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    else if (value is decimal or double or float)
                    {
                        cell.Value = Convert.ToDecimal(value);
                    }
                    else if (value is int or long)
                    {
                        cell.Value = Convert.ToInt64(value);
                    }
                    else
                    {
                        cell.Value = value?.ToString() ?? string.Empty;
                    }

                    col++;
                }
                row++;
            }

            // 自动调整列宽
            worksheet.Columns().AdjustToContents();
        }

        // 确保目录存在
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        workbook.SaveAs(filePath);
    }

    /// <summary>
    /// 导出单个试验记录报告到PDF
    /// </summary>
    /// <param name="record">试验记录</param>
    /// <param name="processData">过程数据（可选）</param>
    /// <param name="filePath">输出文件路径</param>
    public void ExportSingleRecordReport(TestRecord record, TestProcessData? processData, string filePath)
    {
        if (record == null)
            throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        // 初始化QuestPDF许可证
        QuestPDF.Settings.License = LicenseType.Community;

        // 确保目录存在
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(11));

                // 页眉
                page.Header().Element(BuildHeader);

                // 内容
                page.Content().PaddingVertical(20).Column(column =>
                {
                    // 基本信息
                    column.Item().Element(container => BuildBasicInfo(container, record));

                    column.Item().PaddingTop(20).Element(container => BuildTestResults(container, record));

                    // 过程曲线摘要
                    if (processData != null)
                    {
                        column.Item().PaddingTop(20).Element(container => BuildProcessCurveSummary(container, processData));
                    }
                });

                // 页脚
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    text.Span("  |  ");
                    text.Span("隔离泄漏试验报告");
                });
            });
        })
        .GeneratePdf(filePath);
    }

    /// <summary>
    /// 导出单个试验对象的所有历史记录到Excel
    /// </summary>
    /// <param name="objectCode">试验对象编码</param>
    /// <param name="records">该对象的历史记录列表</param>
    /// <param name="filePath">输出文件路径</param>
    public void ExportObjectHistory(string objectCode, IEnumerable<TestRecord> records, string filePath)
    {
        if (string.IsNullOrEmpty(objectCode))
            throw new ArgumentNullException(nameof(objectCode));
        if (records == null)
            throw new ArgumentNullException(nameof(records));
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        using var workbook = new XLWorkbook();

        // 创建工作表1：历史记录汇总
        var summarySheet = workbook.Worksheets.Add("历史记录汇总");
        BuildObjectHistorySummary(summarySheet, objectCode, records);

        // 创建工作表2：详细记录
        var detailSheet = workbook.Worksheets.Add("详细记录");
        BuildObjectHistoryDetail(detailSheet, records);

        // 确保目录存在
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        workbook.SaveAs(filePath);
    }

    #region Private Helper Methods

    private static void BuildHeader(IContainer container)
    {
        container.Background("#4472C4").Padding(20).Column(column =>
        {
            column.Item().Text("隔离泄漏试验报告").FontSize(24).FontColor(Colors.White).Bold();
            column.Item().Text("Isolation Leakage Test Report").FontSize(14).FontColor(Colors.White);
        });
    }

    private static void BuildBasicInfo(IContainer container, TestRecord record)
    {
        container.Border(1).BorderColor("#D0D0D0").Padding(15).Column(column =>
        {
            column.Item().Text("基本信息").FontSize(14).Bold().FontColor("#4472C4");

            column.Item().PaddingTop(10).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                });

                // 行1
                table.Cell().Element(CellStyle).Text("记录编号：");
                table.Cell().Element(CellStyle).Text(record.RecordCode);
                table.Cell().Element(CellStyle).Text("试验时间：");
                table.Cell().Element(CellStyle).Text(record.TestTime.ToString("yyyy-MM-dd HH:mm:ss"));

                // 行2
                table.Cell().Element(CellStyle).Text("项目：");
                table.Cell().Element(CellStyle).Text($"{record.ProjectName ?? record.ProjectCode} ({record.ProjectCode})");
                table.Cell().Element(CellStyle).Text("机组：");
                table.Cell().Element(CellStyle).Text($"{record.UnitName ?? record.UnitCode} ({record.UnitCode})");

                // 行3
                table.Cell().Element(CellStyle).Text("对象编码：");
                table.Cell().Element(CellStyle).Text(record.ObjectCode);
                table.Cell().Element(CellStyle).Text("对象类型：");
                table.Cell().Element(CellStyle).Text(GetObjectTypeText(record.ObjectType));

                // 行4
                table.Cell().Element(CellStyle).Text("测量装置：");
                table.Cell().Element(CellStyle).Text($"{record.Device?.DeviceName ?? record.DeviceCode} ({record.DeviceCode})");
                table.Cell().Element(CellStyle).Text("操作人员：");
                table.Cell().Element(CellStyle).Text(record.Operator);

                // 行5
                table.Cell().Element(CellStyle).Text("导入时间：");
                table.Cell().Element(CellStyle).Text(record.ImportTime.ToString("yyyy-MM-dd HH:mm:ss"));
                table.Cell().Element(CellStyle).Text("备注：");
                table.Cell().Element(CellStyle).Text(record.Remark ?? "无");

                static IContainer CellStyle(IContainer c) => c.Padding(5).BorderRight(1).BorderColor("#E0E0E0");
            });
        });
    }

    private static void BuildTestResults(IContainer container, TestRecord record)
    {
        container.Border(1).BorderColor("#D0D0D0").Padding(15).Column(column =>
        {
            column.Item().Text("试验结果").FontSize(14).Bold().FontColor("#4472C4");

            column.Item().PaddingTop(10).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                });

                // 行1
                table.Cell().Element(CellStyle).Text("试验压力：");
                table.Cell().Element(CellStyle).Text($"{record.TestPressure:F6} MPa");
                table.Cell().Element(CellStyle).Text("泄漏限值：");
                table.Cell().Element(CellStyle).Text($"{record.LeakageLimit:F6} L/min");

                // 行2
                table.Cell().Element(CellStyle).Text("最终泄漏率：");
                table.Cell().Element(CellStyle).Text($"{record.FinalLeakageRate:F6} L/min");
                table.Cell().Element(CellStyle).Text("判定结果：");
                table.Cell().Element(CellStyle).Text(GetResultText(record.Result)).FontColor(record.Result == TestResult.Pass ? "#28A745" : "#DC3545").Bold();

                static IContainer CellStyle(IContainer c) => c.Padding(5).BorderRight(1).BorderColor("#E0E0E0");
            });
        });
    }

    private static void BuildProcessCurveSummary(IContainer container, TestProcessData processData)
    {
        container.Border(1).BorderColor("#D0D0D0").Padding(15).Column(column =>
        {
            column.Item().Text("过程曲线摘要").FontSize(14).Bold().FontColor("#4472C4");

            column.Item().PaddingTop(10).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                });

                // 压力范围
                table.Cell().Element(CellStyle).Text("压力范围：");
                table.Cell().Element(CellStyle).Text($"{processData.PressureMin:F6} ~ {processData.PressureMax:F6} MPa");

                // 流量范围
                table.Cell().Element(CellStyle).Text("流量范围：");
                table.Cell().Element(CellStyle).Text($"{processData.FlowMin:F6} ~ {processData.FlowMax:F6} L/min");

                // 温度范围
                table.Cell().Element(CellStyle).Text("温度范围：");
                table.Cell().Element(CellStyle).Text($"{processData.TempMin:F6} ~ {processData.TempMax:F6} °C");

                static IContainer CellStyle(IContainer c) => c.Padding(5).BorderRight(1).BorderColor("#E0E0E0");
            });

            // 曲线数据说明
            column.Item().PaddingTop(10).Text(text =>
            {
                text.Span("注：完整过程曲线数据包含在原始数据包中。");
                text.Span(" 压力点数：").Bold();
                var pressurePoints = ParseCurvePoints(processData.PressureCurveJson);
                text.Span(pressurePoints?.Length.ToString() ?? "0");
                text.Span(", 流量点数：").Bold();
                var flowPoints = ParseCurvePoints(processData.FlowCurveJson);
                text.Span(flowPoints?.Length.ToString() ?? "0");
                text.Span(", 温度点数：").Bold();
                var tempPoints = ParseCurvePoints(processData.TempCurveJson);
                text.Span(tempPoints?.Length.ToString() ?? "0");
            });
        });
    }

    private static void BuildObjectHistorySummary(IXLWorksheet worksheet, string objectCode, IEnumerable<TestRecord> records)
    {
        var recordList = records.ToList();

        // 标题
        worksheet.Cell(1, 1).Value = $"试验对象历史记录 - {objectCode}";
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;
        worksheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(0x44, 0x72, 0xC4);
        worksheet.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        worksheet.Range(1, 1, 1, 8).Merge();

        // 统计信息
        int totalTests = recordList.Count;
        int passedTests = recordList.Count(r => r.Result == TestResult.Pass);
        int failedTests = recordList.Count(r => r.Result == TestResult.Fail);
        decimal passRate = totalTests > 0 ? Math.Round((decimal)passedTests / totalTests * 100, 2) : 0;

        worksheet.Cell(3, 1).Value = "总试验次数：";
        worksheet.Cell(3, 1).Style.Font.Bold = true;
        worksheet.Cell(3, 2).Value = totalTests;

        worksheet.Cell(3, 3).Value = "合格次数：";
        worksheet.Cell(3, 3).Style.Font.Bold = true;
        worksheet.Cell(3, 4).Value = passedTests;

        worksheet.Cell(4, 1).Value = "不合格次数：";
        worksheet.Cell(4, 1).Style.Font.Bold = true;
        worksheet.Cell(4, 2).Value = failedTests;

        worksheet.Cell(4, 3).Value = "合格率：";
        worksheet.Cell(4, 3).Style.Font.Bold = true;
        worksheet.Cell(4, 4).Value = $"{passRate}%";

        // 表头
        var headers = new[]
        {
            "记录编号", "试验时间", "试验压力", "泄漏限值", "最终泄漏率", "判定结果", "操作人员", "备注"
        };

        int headerRow = 6;
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(headerRow, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x44, 0x72, 0xC4);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // 数据行
        int row = headerRow + 1;
        foreach (var record in recordList.OrderByDescending(r => r.TestTime))
        {
            worksheet.Cell(row, 1).Value = record.RecordCode;
            worksheet.Cell(row, 2).Value = record.TestTime.ToString("yyyy-MM-dd HH:mm:ss");
            worksheet.Cell(row, 3).Value = record.TestPressure;
            worksheet.Cell(row, 4).Value = record.LeakageLimit;
            worksheet.Cell(row, 5).Value = record.FinalLeakageRate;
            worksheet.Cell(row, 6).Value = GetResultText(record.Result);
            worksheet.Cell(row, 7).Value = record.Operator;
            worksheet.Cell(row, 8).Value = record.Remark ?? string.Empty;
            row++;
        }

        worksheet.Columns().AdjustToContents();
    }

    private static void BuildObjectHistoryDetail(IXLWorksheet worksheet, IEnumerable<TestRecord> records)
    {
        var recordList = records.ToList();

        // 表头
        var headers = new[]
        {
            "记录编号", "项目", "机组", "对象编码", "对象类型", "测量装置",
            "试验时间", "试验压力", "泄漏限值", "最终泄漏率", "判定结果", "操作人员", "备注"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x44, 0x72, 0xC4);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // 数据行
        int row = 2;
        foreach (var record in recordList.OrderByDescending(r => r.TestTime))
        {
            worksheet.Cell(row, 1).Value = record.RecordCode;
            worksheet.Cell(row, 2).Value = record.ProjectName ?? record.ProjectCode;
            worksheet.Cell(row, 3).Value = record.UnitName ?? record.UnitCode;
            worksheet.Cell(row, 4).Value = record.ObjectCode;
            worksheet.Cell(row, 5).Value = GetObjectTypeText(record.ObjectType);
            worksheet.Cell(row, 6).Value = record.Device?.DeviceName ?? record.DeviceCode;
            worksheet.Cell(row, 7).Value = record.TestTime.ToString("yyyy-MM-dd HH:mm:ss");
            worksheet.Cell(row, 8).Value = record.TestPressure;
            worksheet.Cell(row, 9).Value = record.LeakageLimit;
            worksheet.Cell(row, 10).Value = record.FinalLeakageRate;
            worksheet.Cell(row, 11).Value = GetResultText(record.Result);
            worksheet.Cell(row, 12).Value = record.Operator;
            worksheet.Cell(row, 13).Value = record.Remark ?? string.Empty;
            row++;
        }

        worksheet.Columns().AdjustToContents();
    }

    private static string GetObjectTypeText(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => "系统",
            PathNodeType.Penetration => "贯穿件",
            PathNodeType.Valve => "阀门",
            PathNodeType.OtherComponent => "其他部件",
            _ => "未知"
        };
    }

    private static string GetResultText(TestResult result)
    {
        return result switch
        {
            TestResult.Pass => "合格",
            TestResult.Fail => "不合格",
            _ => "未知"
        };
    }

    private static double[]? ParseCurvePoints(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            // Simple JSON array parsing for "[1.0,2.0,3.0]" format
            var trimmed = json.Trim('[', ']');
            if (string.IsNullOrWhiteSpace(trimmed))
                return Array.Empty<double>();

            return trimmed.Split(',')
                .Select(s => double.TryParse(s.Trim(), out var v) ? v : 0)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
