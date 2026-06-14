using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 报告导出视图模型（独立页面）
/// </summary>
public sealed class ReportExportViewModel : ViewModelBase
{
    private string _selectedExportScope = "全部试验记录";
    private string _selectedExportFormat = "Excel";
    private string _exportFileName = string.Empty;
    private string _exportStatusMessage = "就绪，请选择导出范围和格式";
    private int _totalRecords;

    public ReportExportViewModel()
    {
        _ = LoadStatisticsAsync();
    }

    public IReadOnlyList<string> ExportScopeOptions { get; } = new List<string>
    {
        "全部试验记录",
        "本月份试验记录",
        "本月合格记录",
        "本月不合格记录"
    };

    public IReadOnlyList<string> ExportFormatOptions { get; } = new List<string>
    {
        "Excel",
        "PDF"
    };

    public string SelectedExportScope
    {
        get => _selectedExportScope;
        set => SetProperty(ref _selectedExportScope, value);
    }

    public string SelectedExportFormat
    {
        get => _selectedExportFormat;
        set => SetProperty(ref _selectedExportFormat, value);
    }

    public string ExportFileName
    {
        get => _exportFileName;
        set => SetProperty(ref _exportFileName, value);
    }

    public string ExportStatusMessage
    {
        get => _exportStatusMessage;
        set => SetProperty(ref _exportStatusMessage, value);
    }

    public int TotalRecords
    {
        get => _totalRecords;
        set => SetProperty(ref _totalRecords, value);
    }

    public string ExportPreviewText => $"将导出 {_totalRecords:N0} 条记录 → {(_selectedExportFormat == "Excel" ? "Excel 工作簿" : "PDF 报告")}";

    public ICommand ExportReportCommand => new RelayCommand(() => _ = ExecuteExportAsync());
    public ICommand QuickExportExcelCommand => new RelayCommand(() => _ = QuickExportAsync("Excel"));
    public ICommand QuickExportPdfCommand => new RelayCommand(() => _ = QuickExportAsync("PDF"));

    private async Task LoadStatisticsAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            TotalRecords = await context.TestRecords.CountAsync();
        }
        catch
        {
            // 静默失败
        }
    }

    private async Task ExecuteExportAsync()
    {
        try
        {
            ExportStatusMessage = "正在生成报告...";

            using var context = DbContextFactory.CreateDbContext();
            var records = await BuildQuery(context).ToListAsync();

            var exportService = new ReportExportService();

            if (SelectedExportFormat == "Excel")
            {
                var fileName = string.IsNullOrWhiteSpace(ExportFileName)
                    ? $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                    : ExportFileName;

                if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    fileName += ".xlsx";

                exportService.ExportTestRecordsToExcel(records, fileName);
                ExportStatusMessage = $"✅ 已成功导出 {records.Count} 条记录到 {fileName}";
            }
            else
            {
                // PDF 导出
                var fileName = string.IsNullOrWhiteSpace(ExportFileName)
                    ? $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
                    : ExportFileName;

                if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    fileName += ".pdf";

                if (records.Any())
                {
                    var firstRecord = records.First();
                    var processData = await context.TestProcessData
                        .FirstOrDefaultAsync(d => d.RecordCode == firstRecord.RecordCode);

                    exportService.ExportSingleRecordReport(firstRecord, processData, fileName);
                    ExportStatusMessage = $"✅ 已成功导出 PDF 报告：{fileName}";
                }
                else
                {
                    ExportStatusMessage = "⚠ 没有可导出的记录";
                }
            }
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"❌ 导出失败：{ex.Message}";
        }
    }

    private async Task QuickExportAsync(string format)
    {
        try
        {
            ExportStatusMessage = $"正在快速导出 {format}...";

            using var context = DbContextFactory.CreateDbContext();
            var records = await BuildQuery(context).ToListAsync();

            if (!records.Any())
            {
                ExportStatusMessage = "⚠ 没有可导出的记录";
                return;
            }

            var exportService = new ReportExportService();
            var fileName = $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}.{(format == "Excel" ? "xlsx" : "pdf")}";

            if (format == "Excel")
            {
                exportService.ExportTestRecordsToExcel(records, fileName);
            }
            else
            {
                var firstRecord = records.First();
                var processData = await context.TestProcessData
                    .FirstOrDefaultAsync(d => d.RecordCode == firstRecord.RecordCode);
                exportService.ExportSingleRecordReport(firstRecord, processData, fileName);
            }

            ExportStatusMessage = $"✅ 快速导出完成：{fileName}";
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"❌ 导出失败：{ex.Message}";
        }
    }

    private IQueryable<TestRecord> BuildQuery(AppDbContext context)
    {
        var query = context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.Device)
            .AsQueryable();

        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);

        switch (SelectedExportScope)
        {
            case "本月份试验记录":
                query = query.Where(r => r.TestTime >= monthStart);
                break;
            case "本月合格记录":
                query = query.Where(r => r.TestTime >= monthStart && r.Result == Models.TestResult.Pass);
                break;
            case "本月不合格记录":
                query = query.Where(r => r.TestTime >= monthStart && r.Result == Models.TestResult.Fail);
                break;
        }

        return query.OrderByDescending(r => r.TestTime);
    }
}
