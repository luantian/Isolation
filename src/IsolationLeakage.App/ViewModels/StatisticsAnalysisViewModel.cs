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
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
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
        public string StatusText => PassRate >= 95 ? "合格" : "不合格";
    };

    /// <summary>
    /// 泄漏率趋势数据
    /// </summary>
    public sealed record LeakageTrendItem(string ObjectCode, DateTime TestTime, decimal LeakageRate, string ValveType);

    /// <summary>
    /// 机组合格情况数据
    /// </summary>
    public sealed record UnitPassItem(string UnitName, int TotalCount, int PassCount, decimal PassRate);

    #endregion

    #region Filter Properties

    /// <summary>
    /// 程序化重建筛选项（加载/刷新下拉列表）期间置为 true。
    /// 此时 ComboBox 因 ItemsSource 被 Clear 而回写 null、或代码还原选中值，
    /// 都不应再触发级联刷新——否则 fire-and-forget 的级联会在还原之后重跑，
    /// 把机组/系统的选中项清空（这是"点查询后内容消失"的根因）。
    /// </summary>
    private bool _isSyncingFilters;

    private string _projectCode = string.Empty;
    public string ProjectCode
    {
        get => _projectCode;
        set
        {
            if (SetProperty(ref _projectCode, value) && !_isSyncingFilters)
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
            if (SetProperty(ref _unitCode, value) && !_isSyncingFilters)
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

    /// <summary>
    /// 下拉筛选项：Code 用于查询与选中值绑定，Display 用于界面展示。
    /// "全部"哨兵项的 Code 也为 "全部"。这样绑定时直接用 Code 做 SelectedValue，
    /// 不再需要把 "编码+名称" 拼成字符串再靠双空格切分还原。
    /// </summary>
    public sealed record FilterOption(string Code, string Display);

    /// <summary>"全部"哨兵的编码</summary>
    private const string AllCode = "全部";

    /// <summary>"全部"哨兵选项（各下拉列表首项）</summary>
    private static readonly FilterOption AllOption = new(AllCode, "全部");

    private ObservableCollection<FilterOption> _availableProjects = [];
    public ObservableCollection<FilterOption> AvailableProjects
    {
        get => _availableProjects;
        set => SetProperty(ref _availableProjects, value);
    }

    private ObservableCollection<FilterOption> _availableUnits = [];
    public ObservableCollection<FilterOption> AvailableUnits
    {
        get => _availableUnits;
        set => SetProperty(ref _availableUnits, value);
    }

    private ObservableCollection<FilterOption> _availableSystems = [];
    public ObservableCollection<FilterOption> AvailableSystems
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

    /// <summary>按阀门类型分组的泄漏率趋势通道（动态多系列图）</summary>
    public ObservableCollection<Controls.TrendChannel> LeakageTrendChannels { get; } = [];

    /// <summary>通道配色板（循环使用）</summary>
    private static readonly System.Windows.Media.Color[] _palette =
    [
        System.Windows.Media.Color.FromRgb(0x07, 0x58, 0xD8), // 蓝
        System.Windows.Media.Color.FromRgb(0x12, 0xA3, 0x66), // 绿
        System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16), // 橙
        System.Windows.Media.Color.FromRgb(0x0E, 0xA5, 0xE9), // 青
        System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6), // 紫
        System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48), // 红
        System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04), // 金
        System.Windows.Media.Color.FromRgb(0x0D, 0x94, 0x88), // 蓝绿
    ];

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

    /// <summary>Tab 4: 阀门试验次数柱状图</summary>
    private PlotModel? _valveTestCountPlotModel;
    public PlotModel? ValveTestCountPlotModel
    {
        get => _valveTestCountPlotModel;
        set => SetProperty(ref _valveTestCountPlotModel, value);
    }

    /// <summary>Tab 5: 机组合格率柱状图</summary>
    private PlotModel? _unitPassPlotModel;
    public PlotModel? UnitPassPlotModel
    {
        get => _unitPassPlotModel;
        set => SetProperty(ref _unitPassPlotModel, value);
    }

    /// <summary>Tab 1: 故障分布堆叠柱状图</summary>
    private PlotModel? _faultDistributionPlotModel;
    public PlotModel? FaultDistributionPlotModel
    {
        get => _faultDistributionPlotModel;
        set => SetProperty(ref _faultDistributionPlotModel, value);
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
            await LoadFilterOptionsInternalAsync(context);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载筛选选项失败（构造阶段）");
        }
    }

    /// <summary>
    /// 内部方法：使用已有的 DbContext 加载筛选选项（避免重复创建连接）
    /// </summary>
    private async Task LoadFilterOptionsInternalAsync(AppDbContext context)
    {
        // 整个重建过程视为程序化同步：期间清空/重填集合、还原选中值都不触发级联
        _isSyncingFilters = true;
        try
        {
            // ✅ 先在数据库层查原始字段，再在内存里构造 FilterOption（EF Core 无法翻译字符串插值）
            var projectList = await context.Projects
                .AsNoTracking()
                .Select(p => new { p.Code, p.Name })
                .OrderBy(p => p.Code)
                .ToListAsync();
            var projects = projectList.Select(p => new FilterOption(p.Code, $"{p.Code}  {p.Name}")).ToList();

            Log.Information("加载到 {Count} 个项目", projects.Count);

            var currentProject = ProjectCode;
            AvailableProjects.Clear();
            AvailableProjects.Add(AllOption);
            foreach (var p in projects) AvailableProjects.Add(p);
            ProjectCode = projects.Any(p => p.Code == currentProject) ? currentProject : AllCode;

            var unitList = await context.Units
                .AsNoTracking()
                .Select(u => new { u.Code, u.Name })
                .OrderBy(u => u.Code)
                .ToListAsync();
            var units = unitList.Select(u => new FilterOption(u.Code, $"{u.Code}  {u.Name}")).ToList();

            Log.Information("加载到 {Count} 个机组", units.Count);

            var currentUnit = UnitCode;
            AvailableUnits.Clear();
            AvailableUnits.Add(AllOption);
            foreach (var u in units) AvailableUnits.Add(u);
            UnitCode = units.Any(u => u.Code == currentUnit) ? currentUnit : AllCode;

            var systemList = await context.TestObjectPathNodes
                .AsNoTracking()
                .Where(n => n.NodeType == PathNodeType.System)
                .Select(n => new { n.Code, n.Name })
                .OrderBy(n => n.Code)
                .ToListAsync();
            var systems = systemList.Select(n => new FilterOption(n.Code, $"{n.Code}  {n.Name}")).ToList();

            Log.Information("加载到 {Count} 个系统", systems.Count);

            var currentSystem = SystemCode;
            AvailableSystems.Clear();
            AvailableSystems.Add(AllOption);
            foreach (var s in systems) AvailableSystems.Add(s);
            SystemCode = systems.Any(s => s.Code == currentSystem) ? currentSystem : AllCode;

            if (projects.Count == 0 && units.Count == 0 && systems.Count == 0)
            {
                Log.Warning("筛选选项全部为空，数据库可能没有基础数据。请先在【基础台账】页面添加项目、机组等信息。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载筛选选项失败");
            throw;
        }
        finally
        {
            _isSyncingFilters = false;
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        // 日期区间校验：起始不得晚于结束
        if (DateFrom.HasValue && DateTo.HasValue && DateFrom.Value.Date > DateTo.Value.Date)
        {
            StatusMessage = "起始日期不能晚于结束日期，请重新选择。";
            return;
        }

        IsLoading = true;
        StatusMessage = "正在加载统计数据...";

        try
        {
            await LoadAllStatisticsAsync();
            StatusMessage = TotalTestCount == 0
                ? "当前筛选条件下无数据，请调整项目/机组/系统或日期范围。"
                : $"数据加载完成 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}（共 {TotalTestCount} 条）";
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
    private async Task ResetFiltersAsync()
    {
        // 重置所有筛选条件
        _isSyncingFilters = true;
        ProjectCode = string.Empty;
        UnitCode = string.Empty;
        SystemCode = string.Empty;
        DateFrom = null;
        DateTo = null;
        _isSyncingFilters = false;

        // 重新加载数据
        await ApplyFiltersAsync();
    }

    // 导出与报告导出页同属数据导出能力，统一要求 ReportExport 权限（此前无守卫，收紧角色后成旁路）
    private bool CanExportData() => Services.Security.PermissionGuard.Can(Services.Security.Perms.ReportExport);

    [RelayCommand(CanExecute = nameof(CanExportData))]
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
            sheetData["汇总"] = await BuildSummarySheetDataAsync(context);

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

    private async Task<List<Dictionary<string, object>>> BuildSummarySheetDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);
        int total = await query.CountAsync();
        int pass = await query.CountAsync(r => r.Result == TestResult.Pass);
        int fail = total - pass;

        return
        [
            new Dictionary<string, object> { { "统计项", "总试验次数" }, { "值", total } },
            new Dictionary<string, object> { { "统计项", "合格次数" }, { "值", pass } },
            new Dictionary<string, object> { { "统计项", "不合格次数" }, { "值", fail } },
            new Dictionary<string, object> { { "统计项", "合格率(%)" }, { "值", total > 0 ? Math.Round((decimal)pass / total * 100, 1) : 0 } },
        ];
    }

    private static string NormalizeValveType(string? type)
    {
        return string.IsNullOrWhiteSpace(type) ? "未知类型" : type.Trim();
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
            .ToListAsync();

        // 阀门类型已改为自由输入：按规范化名称（Trim/空兜底）合并分组，避免手输空格造成碎片化
        stats = stats
            .GroupBy(s => NormalizeValveType(s.TypeName))
            .Select(g => new { TypeName = g.Key, PassCount = g.Sum(x => x.PassCount), FailCount = g.Sum(x => x.FailCount) })
            .OrderByDescending(x => x.PassCount + x.FailCount)
            .ToList();

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
        // 与趋势图同口径：先按时间降序取全局最新 500 条，再在内存中按 对象编码+时间 升序排列输出，
        // 避免按对象编码排序后任意截断导致导出的不是最新数据
        var data = await query
            .Where(r => r.ObjectType == PathNodeType.Valve)
            .OrderByDescending(r => r.TestTime)
            .Select(r => new { r.ObjectCode, r.TestTime, r.FinalLeakageRate, r.TestPressure })
            .Take(500)
            .ToListAsync();

        data.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ObjectCode, b.ObjectCode);
            return c != 0 ? c : a.TestTime.CompareTo(b.TestTime);
        });

        return data.Select(d => new Dictionary<string, object>
        {
            { "对象编码", d.ObjectCode },
            { "试验时间", d.TestTime },
            { "试验压力(kPa)", Helpers.PressureUnitConverter.ToDisplay(d.TestPressure) },
            { "最终泄漏率(Nml/min)", d.FinalLeakageRate },
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
        // 注意：不能用 Task.WhenAll 并行查询，同一个 DbContext 不能同时开多个 DataReader
        using var context = DbContextFactory.CreateDbContext();

        // 仅当下拉选项为空时才重建（例如构造阶段加载失败留下空列表）。
        // 正常情况下不必每次查询都清空/重填，既避免闪烁，也不打无谓的库、
        // 更不会扰动用户已选的机组/系统。
        if (AvailableProjects.Count == 0 || AvailableUnits.Count == 0 || AvailableSystems.Count == 0)
        {
            await LoadFilterOptionsInternalAsync(context);
        }

        await LoadFaultTypeDataAsync(context);
        await LoadValveTestCountsAsync(context);
        await LoadPassRateDataAsync(context);
        await LoadLeakageTrendDataAsync(context);
        await LoadUnitPassDataAsync(context);
    }

    private async Task LoadFaultTypeDataAsync(AppDbContext context)
    {
        var query = BuildFilteredQuery(context);

        var faultTypeStatsRaw = await query
            .Include(r => r.TestObject)
            .Where(r => r.TestObject != null && r.TestObject.NodeType == PathNodeType.Valve)
            .GroupBy(r => r.TestObject!.ValveType ?? "未知类型")
            .Select(g => new
            {
                TypeName = g.Key,
                PassCount = g.Count(r => r.Result == TestResult.Pass),
                FailCount = g.Count(r => r.Result == TestResult.Fail)
            })
            .ToListAsync();

        // 阀门类型已改为自由输入：按规范化名称（Trim/空兜底）合并分组，避免手输空格造成碎片化
        var faultTypeStats = faultTypeStatsRaw
            .GroupBy(s => NormalizeValveType(s.TypeName))
            .Select(g => new { TypeName = g.Key, PassCount = g.Sum(x => x.PassCount), FailCount = g.Sum(x => x.FailCount) })
            .OrderByDescending(x => x.PassCount + x.FailCount)
            .ToList();

        FaultTypeData.Clear();
        foreach (var item in faultTypeStats)
        {
            FaultTypeData.Add(new FaultTypeDataItem(item.TypeName, item.PassCount, item.FailCount));
        }

        // 计算柱状图宽度：按全局最大 TotalCount 比例缩放（旧版 Rectangle 条形图兼容）
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

        // ====== OxyPlot 堆叠柱状图（ECharts 风格） ======
        if (faultTypeStats.Count > 0)
        {
            var model = new PlotModel
            {
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColor.FromRgb(0xE2, 0xE8, 0xF0),
                Padding = new OxyThickness(8, 8, 16, 8),
            };

            // X 轴：阀门类型（底部）
            var catAxis = new CategoryAxis
            {
                Position = AxisPosition.Bottom,
                Title = "阀门类型",
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                Angle = faultTypeStats.Count > 5 ? -30 : 0,
                MajorStep = 1,
                MinorStep = 1,
            };
            foreach (var s in faultTypeStats)
                catAxis.Labels.Add(s.TypeName);
            model.Axes.Add(catAxis);

            // Y 轴：数量（左侧）
            var valAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "试验次数",
                Minimum = 0,
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(0x30, 0xDE, 0xE4, 0xEE),
                MajorGridlineThickness = 1,
                MinorGridlineStyle = LineStyle.None,
            };
            model.Axes.Add(valAxis);

            // 合格柱（绿色，堆叠底部）— 用 RectangleBarSeries 实现竖直柱子
            var passRect = new RectangleBarSeries
            {
                Title = "合格",
                FillColor = OxyColor.FromRgb(0x0E, 0x9F, 0x6E),
                StrokeColor = OxyColor.FromRgb(0x0B, 0x7D, 0x57),
                StrokeThickness = 1,
            };
            // 不合格柱（红色，堆叠顶部）
            var failRect = new RectangleBarSeries
            {
                Title = "不合格",
                FillColor = OxyColor.FromRgb(0xDC, 0x26, 0x26),
                StrokeColor = OxyColor.FromRgb(0xB9, 0x1C, 0x1C),
                StrokeThickness = 1,
            };

            for (int i = 0; i < faultTypeStats.Count; i++)
            {
                var s = faultTypeStats[i];
                double x0 = i - 0.35, x1 = i + 0.35;
                // 合格：从 0 到 passCount
                passRect.Items.Add(new RectangleBarItem(x0, 0, x1, s.PassCount));
                // 不合格：从 passCount 到 passCount + failCount
                failRect.Items.Add(new RectangleBarItem(x0, s.PassCount, x1, s.PassCount + s.FailCount));
            }
            model.Series.Add(passRect);
            model.Series.Add(failRect);

            FaultDistributionPlotModel = model;
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

        // 构建柱状图（Top 20 保证可读性）
        var top20 = testCounts.Take(20).ToList();
        if (top20.Count > 0)
        {
            var model = new PlotModel
            {
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColor.FromRgb(0xE2, 0xE8, 0xF0),
                Padding = new OxyThickness(8, 8, 16, 8),
            };
            var catAxis = new CategoryAxis
            {
                Position = AxisPosition.Bottom,
                Title = "阀门编号",
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                Angle = -35,
            };
            foreach (var t in top20) catAxis.Labels.Add(t.ObjectCode);
            model.Axes.Add(catAxis);

            var valAxis = new LinearAxis
            {
                Position = AxisPosition.Left, Title = "试验次数", Minimum = 0,
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(0x40, 0xDE, 0xE4, 0xEE),
                MinorGridlineStyle = LineStyle.None,
            };
            model.Axes.Add(valAxis);

            var bar = new RectangleBarSeries
            {
                Title = "试验次数",
                FillColor = OxyColor.FromRgb(0x07, 0x58, 0xD8),
                StrokeColor = OxyColor.FromRgb(0x05, 0x42, 0xA0),
                StrokeThickness = 1,
            };
            for (int i = 0; i < top20.Count; i++)
                bar.Items.Add(new RectangleBarItem(i - 0.35, 0, i + 0.35, top20[i].Count));
            model.Series.Add(bar);
            ValveTestCountPlotModel = model;
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

        // 获取阀门的泄漏率历史记录，包含阀门类型。降序先取最新 500 条，再反转为时间升序供图表使用，
        // 避免升序 + Take 把最新数据截掉（超过 500 条时曲线永远停在历史某点）
        var leakageData = await query
            .Where(r => r.ObjectType == PathNodeType.Valve && r.TestObject != null)
            .OrderByDescending(r => r.TestTime)
            .Select(r => new
            {
                r.ObjectCode,
                r.TestTime,
                r.FinalLeakageRate,
                ValveType = r.TestObject!.ValveType ?? "未知类型",
            })
            .Take(500) // 限制数据点数量以保证图表性能
            .ToListAsync();

        leakageData.Reverse();

        LeakageTrendData.Clear();
        foreach (var item in leakageData)
        {
            LeakageTrendData.Add(new LeakageTrendItem(item.ObjectCode, item.TestTime, item.FinalLeakageRate, NormalizeValveType(item.ValveType)));
        }

        // ====== 按阀门类型分组，构建动态多系列通道 ======
        LeakageTrendChannels.Clear();
        LeakageRatePoints.Clear();

        var groupedByType = leakageData.GroupBy(d => NormalizeValveType(d.ValveType)).ToList();

        double globalMin = double.MaxValue, globalMax = double.MinValue;
        int colorIndex = 0;

        foreach (var group in groupedByType)
        {
            var channel = new Controls.TrendChannel
            {
                Name = group.Key,
                Unit = "Nml/min",
                Color = _palette[colorIndex % _palette.Length],
            };
            colorIndex++;

            double chMin = double.MaxValue, chMax = double.MinValue;

            foreach (var item in group)
            {
                double rate = (double)item.FinalLeakageRate;
                channel.Points.Add(rate);
                chMin = Math.Min(chMin, rate);
                chMax = Math.Max(chMax, rate);

                // 同步填充旧的单通道数据（保持向后兼容）
                LeakageRatePoints.Add(rate);
                globalMin = Math.Min(globalMin, rate);
                globalMax = Math.Max(globalMax, rate);
            }

            channel.Min = chMin == double.MaxValue ? 0 : chMin;
            channel.Max = chMax == double.MinValue ? 0 : chMax;

            LeakageTrendChannels.Add(channel);
        }

        if (leakageData.Any())
        {
            double range = globalMax - globalMin;
            if (range == 0) range = 0.001;
            double margin(double v, bool up) => up ? v + range * 0.1 : v - range * 0.1;

            LeakageRateMin = margin(globalMin, false);
            LeakageRateMax = margin(globalMax, true);
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

        // 构建合格率柱状图
        if (unitStats.Count > 0)
        {
            var model = new PlotModel
            {
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = OxyColor.FromRgb(0xE2, 0xE8, 0xF0),
                Padding = new OxyThickness(8, 8, 16, 8),
            };
            var catAxis = new CategoryAxis
            {
                Position = AxisPosition.Bottom,
                Title = "机组",
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                Angle = -25,
            };
            foreach (var s in unitStats) catAxis.Labels.Add(s.UnitName);
            model.Axes.Add(catAxis);

            var valAxis = new LinearAxis
            {
                Position = AxisPosition.Left, Title = "合格率 %",
                Minimum = 0, Maximum = 100,
                TitleColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TextColor = OxyColor.FromRgb(0x64, 0x74, 0x8B),
                TicklineColor = OxyColor.FromRgb(0xC8, 0xD0, 0xDC),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(0x40, 0xDE, 0xE4, 0xEE),
                MinorGridlineStyle = LineStyle.None,
            };
            model.Axes.Add(valAxis);

            // 合格率柱（蓝色）
            var passBar = new RectangleBarSeries
            {
                Title = "合格率",
                FillColor = OxyColor.FromRgb(0x07, 0x58, 0xD8),
                StrokeColor = OxyColor.FromRgb(0x05, 0x42, 0xA0),
                StrokeThickness = 1,
            };
            for (int i = 0; i < unitStats.Count; i++)
                passBar.Items.Add(new RectangleBarItem(i - 0.35, 0, i + 0.35, (double)unitStats[i].PassRate));
            model.Series.Add(passBar);

            UnitPassPlotModel = model;
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

        if (!string.IsNullOrWhiteSpace(ProjectCode) && ProjectCode != AllCode)
        {
            var code = ProjectCode;
            query = query.Where(r => r.ProjectCode == code);
        }

        if (!string.IsNullOrWhiteSpace(UnitCode) && UnitCode != AllCode)
        {
            var code = UnitCode;
            query = query.Where(r => r.UnitCode == code);
        }

        if (!string.IsNullOrWhiteSpace(SystemCode) && SystemCode != AllCode)
        {
            var code = SystemCode;
            // 层级为 系统→贯穿件→阀门/其他部件：试验对象的系统通常是其"父的父"。
            // 兼容对象直接挂在系统下的情况：父节点或祖父节点任一命中即视为属于该系统。
            query = query.Where(r => r.TestObject != null &&
                                     ((r.TestObject.Parent != null && r.TestObject.Parent.Code == code) ||
                                      (r.TestObject.Parent != null && r.TestObject.Parent.Parent != null &&
                                       r.TestObject.Parent.Parent.Code == code)));
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
            var previous = UnitCode;

            // 重填列表时抑制回写/还原引发的级联，最后统一决定机组与系统
            _isSyncingFilters = true;
            try
            {
                AvailableUnits.Clear();
                AvailableUnits.Add(AllOption);
                foreach (var u in units) AvailableUnits.Add(u);
                // 原选中的机组若仍在新列表中则保留，否则回到"全部"
                UnitCode = units.Any(u => u.Code == previous) ? previous : AllCode;
            }
            finally
            {
                _isSyncingFilters = false;
            }

            // 机组确定后再刷新系统（不依赖 ComboBox 回写触发）
            await CascadeRefreshSystemsAsync();
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
            var previous = SystemCode;

            _isSyncingFilters = true;
            try
            {
                AvailableSystems.Clear();
                AvailableSystems.Add(AllOption);
                foreach (var s in systems) AvailableSystems.Add(s);
                SystemCode = systems.Any(s => s.Code == previous) ? previous : AllCode;
            }
            finally
            {
                _isSyncingFilters = false;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "级联刷新系统列表失败");
        }
    }

    #endregion

    #region Helper Methods for Filter Options

    /// <summary>
    /// 获取项目筛选项
    /// </summary>
    public async Task<List<FilterOption>> GetAvailableProjectsAsync()
    {
        using var context = DbContextFactory.CreateDbContext();
        var list = await context.Projects
            .AsNoTracking()
            .Select(p => new { p.Code, p.Name })
            .OrderBy(p => p.Code)
            .ToListAsync();
        return list.Select(p => new FilterOption(p.Code, $"{p.Code}  {p.Name}")).ToList();
    }

    /// <summary>
    /// 获取指定项目下的机组筛选项（projectCode 传编码或"全部"）
    /// </summary>
    public async Task<List<FilterOption>> GetAvailableUnitsAsync(string projectCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var query = context.Units.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(projectCode) && projectCode != AllCode)
        {
            query = query.Where(u => u.ProjectCode == projectCode);
        }

        var list = await query
            .Select(u => new { u.Code, u.Name })
            .OrderBy(u => u.Code)
            .ToListAsync();
        return list.Select(u => new FilterOption(u.Code, $"{u.Code}  {u.Name}")).ToList();
    }

    /// <summary>
    /// 获取指定机组下的系统筛选项（unitCode 传编码或"全部"）
    /// </summary>
    public async Task<List<FilterOption>> GetAvailableSystemsAsync(string unitCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var query = context.TestObjectPathNodes
            .AsNoTracking()
            .Where(n => n.NodeType == PathNodeType.System);

        if (!string.IsNullOrWhiteSpace(unitCode) && unitCode != AllCode)
        {
            query = query.Where(n => n.UnitCode == unitCode);
        }

        var list = await query
            .Select(n => new { n.Code, n.Name })
            .OrderBy(n => n.Code)
            .ToListAsync();
        return list.Select(n => new FilterOption(n.Code, $"{n.Code}  {n.Name}")).ToList();
    }

    #endregion
}
