using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 统计分析视图模型
/// </summary>
public sealed partial class StatisticsAnalysisViewModel : ViewModelBase, IRefreshable
{
    #region Data Models for Charts

    /// <summary>
    /// 故障类型统计数据（需要动态计算 PassWidth/FailWidth，故用 class 而非 record）
    /// </summary>
    public sealed class FaultTypeDataItem : ObservableObject
    {
        public string TypeName { get; }
        public int PassCount { get; }
        public int FailCount { get; }
        public int TotalCount => PassCount + FailCount;

        private double _passWidth;
        public double PassWidth
        {
            get => _passWidth;
            set => SetProperty(ref _passWidth, value);
        }

        private double _failWidth;
        public double FailWidth
        {
            get => _failWidth;
            set => SetProperty(ref _failWidth, value);
        }

        public FaultTypeDataItem(string typeName, int passCount, int failCount)
        {
            TypeName = typeName;
            PassCount = passCount;
            FailCount = failCount;
        }
    }

    /// <summary>
    /// 单个阀门试验次数统计数据
    /// </summary>
    public sealed record ValveTestCountItem(string ObjectCode, int Count);

    /// <summary>
    /// 合格率统计数据
    /// </summary>
    public sealed record PassRateItem(string ObjectCode, decimal PassRate, int TestCount, int PassCount)
    {
        public System.Windows.Media.Brush StatusBrush => PassRate >= 95
            ? System.Windows.Media.Brushes.ForestGreen
            : PassRate >= 80
                ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.Red;
        public string StatusText => PassRate >= 95 ? "合格" : PassRate >= 80 ? "关注" : "异常";
    };

    /// <summary>
    /// 泄漏率趋势数据
    /// </summary>
    public sealed record LeakageTrendItem(string ObjectCode, DateTime TestTime, decimal LeakageRate);

    /// <summary>
    /// 机组合格情况数据
    /// </summary>
    public sealed record UnitPassItem(string UnitName, int TotalCount, int PassCount, decimal PassRate);

    #endregion

    #region Filter Properties

    private string _projectCode = string.Empty;
    public string ProjectCode
    {
        get => _projectCode;
        set
        {
            if (SetProperty(ref _projectCode, value))
            {
                // 级联刷新机组和系统
                _ = CascadeRefreshUnitsAsync();
                SystemCode = string.Empty;
            }
        }
    }

    private string _unitCode = string.Empty;
    public string UnitCode
    {
        get => _unitCode;
        set
        {
            if (SetProperty(ref _unitCode, value))
            {
                // 级联刷新系统
                _ = CascadeRefreshSystemsAsync();
            }
        }
    }

    private string _systemCode = string.Empty;
    public string SystemCode
    {
        get => _systemCode;
        set => SetProperty(ref _systemCode, value);
    }

    private DateTime? _dateFrom;
    public DateTime? DateFrom
    {
        get => _dateFrom;
        set => SetProperty(ref _dateFrom, value);
    }

    private DateTime? _dateTo;
    public DateTime? DateTo
    {
        get => _dateTo;
        set => SetProperty(ref _dateTo, value);
    }

    #endregion

    #region Chart Data Collections

    private ObservableCollection<FaultTypeDataItem> _faultTypeData = [];
    public ObservableCollection<FaultTypeDataItem> FaultTypeData
    {
        get => _faultTypeData;
        set => SetProperty(ref _faultTypeData, value);
    }

    private ObservableCollection<ValveTestCountItem> _valveTestCounts = [];
    public ObservableCollection<ValveTestCountItem> ValveTestCounts
    {
        get => _valveTestCounts;
        set => SetProperty(ref _valveTestCounts, value);
    }

    private ObservableCollection<PassRateItem> _passRateData = [];
    public ObservableCollection<PassRateItem> PassRateData
    {
        get => _passRateData;
        set => SetProperty(ref _passRateData, value);
    }

    private ObservableCollection<LeakageTrendItem> _leakageTrendData = [];
    public ObservableCollection<LeakageTrendItem> LeakageTrendData
    {
        get => _leakageTrendData;
        set => SetProperty(ref _leakageTrendData, value);
    }

    private ObservableCollection<UnitPassItem> _unitPassData = [];
    public ObservableCollection<UnitPassItem> UnitPassData
    {
        get => _unitPassData;
        set => SetProperty(ref _unitPassData, value);
    }

    #endregion

    #region State Properties

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Filter Options

    private ObservableCollection<string> _availableProjects = [];
    public ObservableCollection<string> AvailableProjects
    {
        get => _availableProjects;
        set => SetProperty(ref _availableProjects, value);
    }

    private ObservableCollection<string> _availableUnits = [];
    public ObservableCollection<string> AvailableUnits
    {
        get => _availableUnits;
        set => SetProperty(ref _availableUnits, value);
    }

    private ObservableCollection<string> _availableSystems = [];
    public ObservableCollection<string> AvailableSystems
    {
        get => _availableSystems;
        set => SetProperty(ref _availableSystems, value);
    }

    #endregion

    #region Summary Properties (for Tab 2 stat tiles)

    private int _totalTestCount;
    public int TotalTestCount
    {
        get => _totalTestCount;
        set => SetProperty(ref _totalTestCount, value);
    }

    private string _overallPassRate = "0";
    public string OverallPassRate
    {
        get => _overallPassRate;
        set => SetProperty(ref _overallPassRate, value);
    }

    private int _totalFailCount;
    public int TotalFailCount
    {
        get => _totalFailCount;
        set => SetProperty(ref _totalFailCount, value);
    }

    #endregion

    #region Chart Properties (for Tab 3 MultiChannelLineChart)

    public ObservableCollection<double> LeakageRatePoints { get; } = [];

    private double _leakageRateMin;
    public double LeakageRateMin
    {
        get => _leakageRateMin;
        set => SetProperty(ref _leakageRateMin, value);
    }

    private double _leakageRateMax = 1;
    public double LeakageRateMax
    {
        get => _leakageRateMax;
        set => SetProperty(ref _leakageRateMax, value);
    }

    #endregion

    #region Constructor

    public StatisticsAnalysisViewModel()
    {
        _ = LoadFilterOptionsAsync();
    }

    private async Task LoadFilterOptionsAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // Load projects
            var projects = await context.Projects
                .AsNoTracking()
                .Select(p => p.Code)
                .OrderBy(c => c)
                .ToListAsync();
            AvailableProjects.Clear();
            foreach (var p in projects) AvailableProjects.Add(p);

            // Load units
            var units = await context.Units
                .AsNoTracking()
                .Select(u => u.Code)
                .OrderBy(c => c)
                .ToListAsync();
            AvailableUnits.Clear();
            foreach (var u in units) AvailableUnits.Add(u);

            // Load systems (top-level path nodes)
            var systems = await context.TestObjectPathNodes
                .AsNoTracking()
                .Where(n => n.NodeType == PathNodeType.System)
                .Select(n => n.Code)
                .OrderBy(c => c)
                .ToListAsync();
            AvailableSystems.Clear();
            foreach (var s in systems) AvailableSystems.Add(s);
        }
        catch
        {
            // Silently fail - filter options will be empty
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载统计数据...";

        try
        {
            await LoadAllStatisticsAsync();
            StatusMessage = $"数据加载完成 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx",
            FileName = $"统计分析_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            Title = "导出统计分析报告"
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "已取消导出";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在导出统计数据...";

            // 从数据库查询当前过滤条件下的完整统计数据
            using var context = DbContextFactory.CreateDbContext();

            var sheetData = new Dictionary<string, List<Dictionary<string, object>>>();

            // 1. 汇总统计
            sheetData["汇总"] = BuildSummarySheetData(context);

            // 2. 故障类型统计
            sheetData["故障类型统计"] = await BuildFaultTypeSheetDataAsync(context);

            // 3. 阀门试验次数 Top50
            sheetData["阀门试验次数"] = await BuildValveTestCountSheetDataAsync(context);

            // 4. 合格率统计
            sheetData["合格率统计"] = await BuildPassRateSheetDataAsync(context);

            // 5. 泄漏率趋势
            sheetData["泄漏率趋势"] = await BuildLeakageTrendSheetDataAsync(context);

            // 6. 机组合格情况
            sheetData["机组合格情况"] = await BuildUnitPassSheetDataAsync(context);

            var exportService = new ReportExportService();
            exportService.ExportStatisticsToExcel(sheetData, dialog.FileName);

            StatusMessage = $"✅ 导出完成：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Private Methods

    private List<Dictionary<string, object>> BuildSummarySheetData(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        int total = query.Count();
        int pass = query.Count(r => r.Result == TestResult.Pass);
        int fail = total - pass;

        return
        [
            new Dictionary<string, object> { { "统计项", "总试验次数" }, { "值", total } },
            new Dictionary<string, object> { { "统计项", "合格次数" }, { "值", pass } },
            new Dictionary<string, object> { { "统计项", "不合格次数" }, { "值", fail } },
            new Dictionary<string, object> { { "统计项", "合格率(%)" }, { "值", total > 0 ? Math.Round((decimal)pass / total * 100, 1) : 0 } },
        ];
    }

    private async Task<List<Dictionary<string, object>>> BuildFaultTypeSheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        var stats = await query
            .Include(r => r.TestObject)
            .Where(r => r.TestObject != null && r.TestObject.NodeType == PathNodeType.Valve)
            .GroupBy(r => r.TestObject!.ValveType ?? "未知类型")
            .Select(g => new
            {
                TypeName = g.Key,
                PassCount = g.Count(r => r.Result == TestResult.Pass),
                FailCount = g.Count(r => r.Result == TestResult.Fail),
            })
            .OrderByDescending(x => x.PassCount + x.FailCount)
            .ToListAsync();

        var rows = new List<Dictionary<string, object>>();
        foreach (var s in stats)
        {
            rows.Add(new Dictionary<string, object>
            {
                { "阀门类型", s.TypeName },
                { "合格", s.PassCount },
                { "不合格", s.FailCount },
                { "合计", s.PassCount + s.FailCount },
            });
        }
        return rows;
    }

    private async Task<List<Dictionary<string, object>>> BuildValveTestCountSheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        var stats = await query
            .GroupBy(r => r.ObjectCode)
            .Select(g => new { ObjectCode = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(50)
            .ToListAsync();

        return stats.Select(s => new Dictionary<string, object>
            { { "对象编码", s.ObjectCode }, { "试验次数", s.Count } })
            .ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildPassRateSheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        var stats = await query
            .GroupBy(r => r.ObjectCode)
            .Select(g => new
            {
                ObjectCode = g.Key,
                TotalCount = g.Count(),
                PassCount = g.Count(r => r.Result == TestResult.Pass),
            })
            .Where(x => x.TotalCount > 0)
            .Select(x => new
            {
                x.ObjectCode,
                x.TotalCount,
                x.PassCount,
                PassRate = (decimal)x.PassCount / x.TotalCount * 100,
            })
            .OrderBy(x => x.ObjectCode)
            .ToListAsync();

        return stats.Select(s => new Dictionary<string, object>
        {
            { "对象编码", s.ObjectCode },
            { "试验次数", s.TotalCount },
            { "合格次数", s.PassCount },
            { "合格率(%)", Math.Round(s.PassRate, 2) },
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildLeakageTrendSheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        var data = await query
            .Where(r => r.ObjectType == PathNodeType.Valve)
            .OrderBy(r => r.ObjectCode)
            .ThenBy(r => r.TestTime)
            .Select(r => new { r.ObjectCode, r.TestTime, r.FinalLeakageRate, r.TestPressure })
            .Take(500)
            .ToListAsync();

        return data.Select(d => new Dictionary<string, object>
        {
            { "对象编码", d.ObjectCode },
            { "试验时间", d.TestTime },
            { "试验压力(MPa)", d.TestPressure },
            { "最终泄漏率(L/min)", d.FinalLeakageRate },
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildUnitPassSheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        var stats = await query
            .Include(r => r.Unit)
            .GroupBy(r => new
            {
                UnitCode = r.UnitCode,
                UnitName = r.Unit != null ? r.Unit.Name : r.UnitCode,
            })
            .Select(g => new
            {
                g.Key.UnitName,
                TotalCount = g.Count(),
                PassCount = g.Count(r => r.Result == TestResult.Pass),
            })
            .Where(x => x.TotalCount > 0)
            .Select(x => new
            {
                x.UnitName,
                x.TotalCount,
                x.PassCount,
                PassRate = (decimal)x.PassCount / x.TotalCount * 100,
            })
            .OrderBy(x => x.UnitName)
            .ToListAsync();

        return stats.Select(s => new Dictionary<string, object>
        {
            { "机组", s.UnitName },
            { "试验次数", s.TotalCount },
            { "合格次数", s.PassCount },
            { "合格率(%)", Math.Round(s.PassRate, 2) },
        }).ToList();
    }

    #endregion

    #region Private Methods

    Task IRefreshable.RefreshAsync() => LoadAllStatisticsAsync();

    private async Task LoadAllStatisticsAsync()
    {
        using var context = DbContextFactory.CreateDbContext();

        // Load statistics in parallel for better performance
        var faultTypeTask = LoadFaultTypeDataAsync(context);
        var valveTestCountsTask = LoadValveTestCountsAsync(context);
        var passRateTask = LoadPassRateDataAsync(context);
        var leakageTrendTask = LoadLeakageTrendDataAsync(context);
        var unitPassTask = LoadUnitPassDataAsync(context);

        await Task.WhenAll(faultTypeTask, valveTestCountsTask, passRateTask, leakageTrendTask, unitPassTask);
    }

    private async Task LoadFaultTypeDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        var faultTypeStats = await query
            .Include(r => r.TestObject)
            .Where(r => r.TestObject != null && r.TestObject.NodeType == PathNodeType.Valve)
            .GroupBy(r => r.TestObject!.ValveType ?? "未知类型")
            .Select(g => new
            {
                TypeName = g.Key,
                PassCount = g.Count(r => r.Result == TestResult.Pass),
                FailCount = g.Count(r => r.Result == TestResult.Fail)
            })
            .OrderByDescending(x => x.PassCount + x.FailCount)
            .ToListAsync();

        FaultTypeData.Clear();
        foreach (var item in faultTypeStats)
        {
            FaultTypeData.Add(new FaultTypeDataItem(item.TypeName, item.PassCount, item.FailCount));
        }

        // 计算柱状图宽度：按全局最大 TotalCount 比例缩放
        const double MaxBarWidth = 200.0;
        var maxTotal = FaultTypeData.Count > 0 ? FaultTypeData.Max(x => x.TotalCount) : 0;
        if (maxTotal > 0)
        {
            foreach (var item in FaultTypeData)
            {
                item.PassWidth = (double)item.PassCount / maxTotal * MaxBarWidth;
                item.FailWidth = (double)item.FailCount / maxTotal * MaxBarWidth;
            }
        }
    }

    private async Task LoadValveTestCountsAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        var testCounts = await query
            .GroupBy(r => r.ObjectCode)
            .Select(g => new
            {
                ObjectCode = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(50) // Limit to top 50 for chart readability
            .ToListAsync();

        ValveTestCounts.Clear();
        foreach (var item in testCounts)
        {
            ValveTestCounts.Add(new ValveTestCountItem(item.ObjectCode, item.Count));
        }
    }

    private async Task LoadPassRateDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        var passRateStats = await query
            .GroupBy(r => r.ObjectCode)
            .Select(g => new
            {
                ObjectCode = g.Key,
                TotalCount = g.Count(),
                PassCount = g.Count(r => r.Result == TestResult.Pass)
            })
            .Where(x => x.TotalCount > 0)
            .Select(x => new
            {
                x.ObjectCode,
                x.TotalCount,
                x.PassCount,
                PassRate = (decimal)x.PassCount / x.TotalCount * 100
            })
            .OrderBy(x => x.ObjectCode)
            .ToListAsync();

        // Update summary stats
        var allRecords = await query.CountAsync();
        var allPass = await query.CountAsync(r => r.Result == TestResult.Pass);
        var allFail = allRecords - allPass;

        TotalTestCount = allRecords;
        TotalFailCount = allFail;
        OverallPassRate = allRecords > 0
            ? Math.Round((decimal)allPass / allRecords * 100, 1).ToString()
            : "0";

        PassRateData.Clear();
        foreach (var item in passRateStats)
        {
            PassRateData.Add(new PassRateItem(item.ObjectCode, Math.Round(item.PassRate, 2), item.TotalCount, item.PassCount));
        }
    }

    private async Task LoadLeakageTrendDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        // 获取阀门的泄漏率历史记录，按时间排序
        var leakageData = await query
            .Where(r => r.ObjectType == PathNodeType.Valve)
            .OrderBy(r => r.TestTime)
            .Select(r => new
            {
                r.ObjectCode,
                r.TestTime,
                r.FinalLeakageRate,
            })
            .Take(500) // 限制数据点数量以保证图表性能
            .ToListAsync();

        LeakageTrendData.Clear();
        foreach (var item in leakageData)
        {
            LeakageTrendData.Add(new LeakageTrendItem(item.ObjectCode, item.TestTime, item.FinalLeakageRate));
        }

        // 填充图表数据点
        LeakageRatePoints.Clear();

        double min = double.MaxValue, max = double.MinValue;

        foreach (var item in leakageData)
        {
            double rate = (double)item.FinalLeakageRate;
            LeakageRatePoints.Add(rate);
            min = Math.Min(min, rate);
            max = Math.Max(max, rate);
        }

        if (leakageData.Any())
        {
            double range = max - min;
            if (range == 0) range = 0.001;
            double margin(double v, bool up) => up ? v + range * 0.1 : v - range * 0.1;

            LeakageRateMin = margin(min, false);
            LeakageRateMax = margin(max, true);
        }
    }

    private async Task LoadUnitPassDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        var unitStats = await query
            .Include(r => r.Unit)
            .GroupBy(r => new
            {
                UnitCode = r.UnitCode,
                UnitName = r.Unit != null ? r.Unit.Name : r.UnitCode
            })
            .Select(g => new
            {
                g.Key.UnitCode,
                g.Key.UnitName,
                TotalCount = g.Count(),
                PassCount = g.Count(r => r.Result == TestResult.Pass)
            })
            .Where(x => x.TotalCount > 0)
            .Select(x => new
            {
                x.UnitName,
                x.TotalCount,
                x.PassCount,
                PassRate = (decimal)x.PassCount / x.TotalCount * 100
            })
            .OrderBy(x => x.UnitName)
            .ToListAsync();

        UnitPassData.Clear();
        foreach (var item in unitStats)
        {
            UnitPassData.Add(new UnitPassItem(item.UnitName, item.TotalCount, item.PassCount, Math.Round(item.PassRate, 2)));
        }
    }

    /// <summary>
    /// Builds a filtered query based on current filter settings
    /// </summary>
    private IQueryable<TestRecord> BuildFilteredQuery(AppDbContext context)
    {
        IQueryable<TestRecord> query = context.TestRecords
            .AsNoTracking()
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.TestObject)
            .Include(r => r.Device);

        if (!string.IsNullOrWhiteSpace(ProjectCode))
        {
            query = query.Where(r => r.ProjectCode == ProjectCode);
        }

        if (!string.IsNullOrWhiteSpace(UnitCode))
        {
            query = query.Where(r => r.UnitCode == UnitCode);
        }

        if (!string.IsNullOrWhiteSpace(SystemCode))
        {
            // Filter by system code - need to traverse through TestObjectPathNode
            query = query.Where(r => r.TestObject != null &&
                                     r.TestObject.Parent != null &&
                                     r.TestObject.Parent.Code == SystemCode);
        }

        if (DateFrom.HasValue)
        {
            query = query.Where(r => r.TestTime >= DateFrom.Value);
        }

        if (DateTo.HasValue)
        {
            // Include the entire end date
            var endDate = DateTo.Value.Date.AddDays(1).AddTicks(-1);
            query = query.Where(r => r.TestTime <= endDate);
        }

        return query;
    }

    /// <summary>级联刷新机组下拉（项目变更时调用）</summary>
    private async Task CascadeRefreshUnitsAsync()
    {
        try
        {
            var units = await GetAvailableUnitsAsync(ProjectCode);
            AvailableUnits.Clear();
            foreach (var u in units) AvailableUnits.Add(u);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "级联刷新机组列表失败");
        }
    }

    /// <summary>级联刷新系统下拉（机组变更时调用）</summary>
    private async Task CascadeRefreshSystemsAsync()
    {
        try
        {
            var systems = await GetAvailableSystemsAsync(UnitCode);
            AvailableSystems.Clear();
            foreach (var s in systems) AvailableSystems.Add(s);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "级联刷新系统列表失败");
        }
    }

    #endregion

    #region Helper Methods for Filter Options

    /// <summary>
    /// Gets available project codes for filter dropdown
    /// </summary>
    public async Task<List<string>> GetAvailableProjectsAsync()
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.Projects
            .AsNoTracking()
            .Select(p => p.Code)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    /// <summary>
    /// Gets available unit codes for a given project
    /// </summary>
    public async Task<List<string>> GetAvailableUnitsAsync(string projectCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var query = context.Units.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(projectCode))
        {
            query = query.Where(u => u.ProjectCode == projectCode);
        }

        return await query
            .Select(u => u.Code)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    /// <summary>
    /// Gets available system codes for a given unit
    /// </summary>
    public async Task<List<string>> GetAvailableSystemsAsync(string unitCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var query = context.TestObjectPathNodes
            .AsNoTracking()
            .Where(n => n.NodeType == PathNodeType.System);

        if (!string.IsNullOrWhiteSpace(unitCode))
        {
            query = query.Where(n => n.UnitCode == unitCode);
        }

        return await query
            .Select(n => n.Code)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    #endregion
}
