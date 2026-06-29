using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

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
    private string _exportDirectory = string.Empty;

    public ReportExportViewModel()
    {
        // 加载已保存的导出目录（空则用"我的文档"默认）
        _exportDirectory = Configuration.AppConfiguration.GetUserSettings().Export.ExportDirectory;
        if (string.IsNullOrWhiteSpace(_exportDirectory))
            _exportDirectory = DefaultExportDirectory();

        _ = LoadStatisticsAsync();
    }

    /// <summary>默认导出目录：我的文档。</summary>
    private static string DefaultExportDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>导出目录（持久化）。快速导出直接用它；完整导出用它作默认目录。</summary>
    public string ExportDirectory
    {
        get => _exportDirectory;
        set => SetProperty(ref _exportDirectory, value);
    }

    /// <summary>选择导出目录命令</summary>
    public ICommand BrowseExportDirectoryCommand => new RelayCommand(BrowseExportDirectory);

    private void BrowseExportDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择导出目录",
            InitialDirectory = Directory.Exists(ExportDirectory) ? ExportDirectory : DefaultExportDirectory(),
        };
        if (dialog.ShowDialog() == true)
        {
            ExportDirectory = dialog.FolderName;
            // 持久化保存
            var settings = Configuration.AppConfiguration.GetUserSettings();
            settings.Export.ExportDirectory = ExportDirectory;
            Configuration.AppConfiguration.SaveUserSettings(settings);
            ExportStatusMessage = $"✅ 导出目录已设为：{ExportDirectory}";
        }
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
            ExportStatusMessage = "正在查询数据...";

            using var context = DbContextFactory.CreateDbContext();
            var records = await BuildQuery(context).ToListAsync();

            if (!records.Any())
            {
                ExportStatusMessage = "⚠ 没有可导出的记录";
                return;
            }

            var exportService = new ReportExportService();

            if (SelectedExportFormat == "Excel")
            {
                var defaultName = string.IsNullOrWhiteSpace(ExportFileName)
                    ? $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : ExportFileName;
                if (!defaultName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    defaultName += ".xlsx";

                var dialog = new SaveFileDialog
                {
                    Title = "导出 Excel 报告",
                    Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                    FileName = defaultName,
                    DefaultExt = ".xlsx",
                    InitialDirectory = Directory.Exists(ExportDirectory) ? ExportDirectory : DefaultExportDirectory()
                };

                if (dialog.ShowDialog() != true)
                {
                    ExportStatusMessage = "已取消导出";
                    return;
                }

                ExportStatusMessage = "正在生成 Excel...";
                exportService.ExportTestRecordsToExcel(records, dialog.FileName);
                ExportStatusMessage = $"✅ 已成功导出 {records.Count} 条记录到 {dialog.FileName}";
            }
            else
            {
                var defaultName = string.IsNullOrWhiteSpace(ExportFileName)
                    ? $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : ExportFileName;
                if (!defaultName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    defaultName += ".pdf";

                var dialog = new SaveFileDialog
                {
                    Title = "导出 PDF 报告",
                    Filter = "PDF 报告 (*.pdf)|*.pdf",
                    FileName = defaultName,
                    DefaultExt = ".pdf",
                    InitialDirectory = Directory.Exists(ExportDirectory) ? ExportDirectory : DefaultExportDirectory()
                };

                if (dialog.ShowDialog() != true)
                {
                    ExportStatusMessage = "已取消导出";
                    return;
                }

                ExportStatusMessage = $"正在生成 PDF（{records.Count} 条记录）...";

                // 批量查询过程数据
                var recordCodes = records.Select(r => r.RecordCode).ToList();
                var processDataList = await context.TestProcessData
                    .Where(d => recordCodes.Contains(d.RecordCode))
                    .ToDictionaryAsync(d => d.RecordCode);

                var items = records.Select(r =>
                    (r, processDataList.GetValueOrDefault(r.RecordCode))).ToList();

                exportService.ExportBatchPdfReport(items, dialog.FileName);
                ExportStatusMessage = $"✅ 已成功导出 {records.Count} 条记录到 PDF：{dialog.FileName}";
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
            var baseDir = string.IsNullOrWhiteSpace(ExportDirectory) ? DefaultExportDirectory() : ExportDirectory;
            Directory.CreateDirectory(baseDir); // 确保目录存在
            var fileName = $"试验报告_{DateTime.Now:yyyyMMdd_HHmmss}.{(format == "Excel" ? "xlsx" : "pdf")}";
            var filePath = Path.Combine(baseDir, fileName);

            if (format == "Excel")
            {
                exportService.ExportTestRecordsToExcel(records, filePath);
            }
            else
            {
                var recordCodes = records.Select(r => r.RecordCode).ToList();
                var processDataList = await context.TestProcessData
                    .Where(d => recordCodes.Contains(d.RecordCode))
                    .ToDictionaryAsync(d => d.RecordCode);

                var items = records.Select(r =>
                    (r, processDataList.GetValueOrDefault(r.RecordCode))).ToList();

                exportService.ExportBatchPdfReport(items, filePath);
            }

            ExportStatusMessage = $"✅ 快速导出完成：{filePath}";
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
