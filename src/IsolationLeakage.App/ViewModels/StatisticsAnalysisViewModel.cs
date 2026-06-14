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
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 统计分析视图模型
/// </summary>
public sealed partial class StatisticsAnalysisViewModel : ViewModelBase
{
    #region Data Models for Charts

    /// <summary>
    /// 故障类型统计数据
    /// </summary>
    public sealed record FaultTypeDataItem(string TypeName, int PassCount, int FailCount)
    {
        public int TotalCount => PassCount + FailCount;
    };

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
        set => SetProperty(ref _projectCode, value);
    }

    private string _unitCode = string.Empty;
    public string UnitCode
    {
        get => _unitCode;
        set => SetProperty(ref _unitCode, value);
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

    public ObservableCollection<double> PressureLeakagePoints { get; } = [];
    public ObservableCollection<double> FlowLeakagePoints { get; } = [];
    public ObservableCollection<double> TempLeakagePoints { get; } = [];

    private double _pressureMin;
    public double PressureMin
    {
        get => _pressureMin;
        set => SetProperty(ref _pressureMin, value);
    }

    private double _pressureMax = 1;
    public double PressureMax
    {
        get => _pressureMax;
        set => SetProperty(ref _pressureMax, value);
    }

    private double _flowMin;
    public double FlowMin
    {
        get => _flowMin;
        set => SetProperty(ref _flowMin, value);
    }

    private double _flowMax = 1;
    public double FlowMax
    {
        get => _flowMax;
        set => SetProperty(ref _flowMax, value);
    }

    private double _tempMin;
    public double TempMin
    {
        get => _tempMin;
        set => SetProperty(ref _tempMin, value);
    }

    private double _tempMax = 1;
    public double TempMax
    {
        get => _tempMax;
        set => SetProperty(ref _tempMax, value);
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
        // TODO: Implement export functionality
        await Task.CompletedTask;
        StatusMessage = "导出功能待实现";
    }

    #endregion

    #region Private Methods

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

        // Get leakage rate history for valves, ordered by time
        var leakageData = await query
            .Where(r => r.ObjectType == PathNodeType.Valve)
            .OrderBy(r => r.ObjectCode)
            .ThenBy(r => r.TestTime)
            .Select(r => new
            {
                r.ObjectCode,
                r.TestTime,
                r.FinalLeakageRate,
                r.TestPressure
            })
            .Take(500) // Limit data points for chart performance
            .ToListAsync();

        LeakageTrendData.Clear();
        foreach (var item in leakageData)
        {
            LeakageTrendData.Add(new LeakageTrendItem(item.ObjectCode, item.TestTime, item.FinalLeakageRate));
        }

        // Populate chart data points
        PressureLeakagePoints.Clear();
        FlowLeakagePoints.Clear();
        TempLeakagePoints.Clear();

        double pMin = double.MaxValue, pMax = double.MinValue;
        double fMin = double.MaxValue, fMax = double.MinValue;
        double tMin = double.MaxValue, tMax = double.MinValue;

        foreach (var item in leakageData)
        {
            double pressure = (double)item.TestPressure;
            double flow = (double)item.FinalLeakageRate;
            double temp = 24.0 + (flow * 10); // simulated temperature correlated to flow

            PressureLeakagePoints.Add(pressure);
            FlowLeakagePoints.Add(flow);
            TempLeakagePoints.Add(temp);

            pMin = Math.Min(pMin, pressure); pMax = Math.Max(pMax, pressure);
            fMin = Math.Min(fMin, flow); fMax = Math.Max(fMax, flow);
            tMin = Math.Min(tMin, temp); tMax = Math.Max(tMax, temp);
        }

        if (leakageData.Any())
        {
            double margin(double v, double range, bool up) => up ? v + range * 0.1 : v - range * 0.1;
            double pRange = pMax - pMin; if (pRange == 0) pRange = 0.1;
            double fRange = fMax - fMin; if (fRange == 0) fRange = 0.001;
            double tRange = tMax - tMin; if (tRange == 0) tRange = 0.5;

            PressureMin = margin(pMin, pRange, false);
            PressureMax = margin(pMax, pRange, true);
            FlowMin = margin(fMin, fRange, false);
            FlowMax = margin(fMax, fRange, true);
            TempMin = margin(tMin, tRange, false);
            TempMax = margin(tMax, tRange, true);
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
