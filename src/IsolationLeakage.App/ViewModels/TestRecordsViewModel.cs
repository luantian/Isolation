using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using IsolationLeakage.App.Views;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验记录视图模型（简化版 - 只负责记录查询和详情展示）
/// </summary>
public sealed partial class TestRecordsViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private TestRecord? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "全部";
    private string? _selectedProjectCode;
    private string? _selectedUnitCode;
    private DateTime? _dateFrom;
    private DateTime? _dateTo;

    /// <summary>波形显示时长（秒），默认 600 秒（10 分钟）</summary>
    [ObservableProperty]
    private int _displayDurationSeconds = 600;

    /// <summary>回放起始时间（秒），默认 0</summary>
    [ObservableProperty]
    private double _playbackStartTime = 0;

    /// <summary>回放结束时间（秒），默认 0（0 表示全部）</summary>
    [ObservableProperty]
    private double _playbackEndTime = 0;

    /// <summary>原始全量时间轴数据（用于恢复）</summary>
    private List<double> _originalTimeAxis = [];

    /// <summary>原始全量通道数据（用于恢复）</summary>
    private readonly Dictionary<string, List<double>> _originalChannelData = new();

    partial void OnDisplayDurationSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(CurveInfoText));
    }

    /// <summary>曲线信息文本</summary>
    public string CurveInfoText => $"显示窗口 {DisplayDurationSeconds}s";

    /// <summary>应用显示时长命令（确认按钮触发，按新窗口裁剪数据）</summary>
    [RelayCommand]
    private void ApplyDisplayDuration()
    {
        if (!HasCurveData || _originalTimeAxis.Count == 0) return;

        double totalDuration = _originalTimeAxis[_originalTimeAxis.Count - 1];
        double startTime = PlaybackStartTime;
        double endTime = PlaybackEndTime > 0 ? PlaybackEndTime : totalDuration;

        // 验证输入
        if (startTime < 0) startTime = 0;
        if (endTime > totalDuration) endTime = totalDuration;
        if (startTime >= endTime)
        {
            Log.Warning("[试验记录] 时间区间无效：{Start}s ~ {End}s, 总时长{Total}s", startTime, endTime, totalDuration);
            return;
        }

        // 从原始数据中找到区间内的点
        int startIndex = 0;
        for (int i = 0; i < _originalTimeAxis.Count; i++)
        {
            if (_originalTimeAxis[i] >= startTime) { startIndex = i; break; }
        }
        int endIndex = _originalTimeAxis.Count - 1;
        for (int i = _originalTimeAxis.Count - 1; i >= 0; i--)
        {
            if (_originalTimeAxis[i] <= endTime) { endIndex = i; break; }
        }

        Log.Information("[试验记录] 时间区间：{Start}s ~ {End}s, startIndex={StartIdx}, endIndex={EndIdx}, 原始总点数={Total}",
            startTime, endTime, startIndex, endIndex, _originalTimeAxis.Count);

        if (startIndex >= endIndex)
        {
            Log.Warning("[试验记录] startIndex >= endIndex，无法裁剪");
            return;
        }
        int keepCount = endIndex - startIndex + 1;

        // 从原始数据裁剪时间轴
        TimeAxisPoints.BeginBatchUpdate();
        try
        {
            var cropped = new List<double>(keepCount);
            for (int i = startIndex; i <= endIndex; i++)
                cropped.Add(_originalTimeAxis[i]);
            TimeAxisPoints.ReplaceAll(cropped);
        }
        finally
        {
            TimeAxisPoints.EndBatchUpdate();
        }

        // 从原始数据裁剪分组集合
        TrimChannelsBatchFromOriginal(PressureChannels, startIndex, keepCount);
        TrimChannelsBatchFromOriginal(TempChannels, startIndex, keepCount);
        TrimChannelsBatchFromOriginal(FlowChannels, startIndex, keepCount);

        Log.Information("[试验记录] 显示时长已应用：{Start}s ~ {End}s, 保留{Keep}点", startTime, endTime, keepCount);
    }

    private void TrimChannelsBatchFromOriginal(ObservableCollection<Controls.TrendChannel> channels, int startIndex, int keepCount)
    {
        foreach (var ch in channels)
        {
            if (!_originalChannelData.TryGetValue(ch.Name, out var originalData))
                continue;

            ch.Points.BeginBatchUpdate();
            try
            {
                var cropped = new List<double>(keepCount);
                for (int i = startIndex; i < startIndex + keepCount && i < originalData.Count; i++)
                    cropped.Add(originalData[i]);
                ch.Points.ReplaceAll(cropped);
            }
            finally
            {
                ch.Points.EndBatchUpdate();
            }
        }
    }
    private string _importTimeFilter = "全部";
    private bool _isLoading;
    private bool _suppressChartUpdate;
    private string _statusMessage = "加载中...";
    private int _totalCount;
    private int _currentPage = 1;
    private int _pageSize = 8; // 默认8条，适配1600宽度

    // 曲线数据（动态通道 + 时间轴）
    private BulkObservableCollection<double> _timeAxisPoints = [];
    /// <summary>曲线数据缓存（LRU 策略，最多保留 50 条记录，防止长时间运行内存耗尽）</summary>
    private readonly System.Collections.Generic.LinkedList<(string RecordCode, TestProcessData Data)> _curveCacheList = new();
    private readonly Dictionary<string, System.Collections.Generic.LinkedListNode<(string RecordCode, TestProcessData Data)>> _curveCache = new();
    private const int MaxCacheSize = 50;

    public BulkObservableCollection<double> TimeAxisPoints
    {
        get => _timeAxisPoints;
        private set => SetProperty(ref _timeAxisPoints, value);
    }

    /// <summary>动态通道集合：从 ChannelsJson 或旧列自动构建，绑定到 TrendChart + 图例。</summary>
    public ObservableCollection<Controls.TrendChannel> DynamicChannels { get; } = [];

    /// <summary>压力分组通道——绑定"压力"图表。</summary>
    public ObservableCollection<Controls.TrendChannel> PressureChannels { get; } = [];
    /// <summary>温度分组通道——绑定"温度"图表。</summary>
    public ObservableCollection<Controls.TrendChannel> TempChannels { get; } = [];
    /// <summary>流量分组通道——绑定"流量"图表。</summary>
    public ObservableCollection<Controls.TrendChannel> FlowChannels { get; } = [];

    /// <summary>按通道名称关键词归入压力/温度/流量三组之一。</summary>
    private ObservableCollection<Controls.TrendChannel> GroupCollectionFor(string name)
    {
        var s = name.ToLowerInvariant();
        if (s.Contains("pressure") || s.Contains("压力") || s.Contains("p1") || s.Contains("p2")) return PressureChannels;
        if (s.Contains("temp") || s.Contains("温度") || s.Contains(" t_") || s == "t") return TempChannels;
        return FlowChannels;
    }

    // 曲线范围属性已迁移到 TrendChannel.Min/Max，不再需要独立属性
    private bool _hasCurveData;
    private bool _isLoadingCurve;

    /// <summary>图表是否正在加载（数据量大时显示加载提示，禁止用户操作）</summary>
    public bool IsLoadingCurve
    {
        get => _isLoadingCurve;
        set => SetProperty(ref _isLoadingCurve, value);
    }

    // 配方参数（从快照中解析）
    private decimal? _recipeLeakageLimit;
    private decimal? _recipePrechargeP2;
    private string? _recipeSystem;
    private decimal? _recipePenetrationDiameter;
    private string? _recipeValveNo;
    private decimal? _recipeValveNominalDiameter;
    private string? _recipeRemark;

    /// <summary>
    /// 配方泄漏率限值（从快照解析）
    /// </summary>
    public decimal? RecipeLeakageLimit
    {
        get => _recipeLeakageLimit;
        private set => SetProperty(ref _recipeLeakageLimit, value);
    }

    /// <summary>
    /// 配方预充压P2（从快照解析）
    /// </summary>
    public decimal? RecipePrechargeP2
    {
        get => _recipePrechargeP2;
        private set => SetProperty(ref _recipePrechargeP2, value);
    }

    /// <summary>
    /// 配方系统（从快照解析）
    /// </summary>
    public string? RecipeSystem
    {
        get => _recipeSystem;
        private set => SetProperty(ref _recipeSystem, value);
    }

    /// <summary>
    /// 贯穿件直径（从快照解析）
    /// </summary>
    public decimal? RecipePenetrationDiameter
    {
        get => _recipePenetrationDiameter;
        private set => SetProperty(ref _recipePenetrationDiameter, value);
    }

    /// <summary>
    /// 试验阀门编号（从快照解析）
    /// </summary>
    public string? RecipeValveNo
    {
        get => _recipeValveNo;
        private set => SetProperty(ref _recipeValveNo, value);
    }

    /// <summary>
    /// 阀门公称直径（从快照解析）
    /// </summary>
    public decimal? RecipeValveNominalDiameter
    {
        get => _recipeValveNominalDiameter;
        private set => SetProperty(ref _recipeValveNominalDiameter, value);
    }

    /// <summary>
    /// 配方备注（从快照解析）
    /// </summary>
    public string? RecipeRemark
    {
        get => _recipeRemark;
        private set => SetProperty(ref _recipeRemark, value);
    }

    public TestRecordsViewModel()
    {
        ResultOptions = ["全部", "合格", "不合格", "未知"];
        _filteredRecords = [];
        _projectCache = new();
        _unitCache = new();
        _recipeCache = new();
        _ = LoadDataAsync();
        _ = LoadLookupCacheAsync(); // 异步缓存 Project/Unit/Recipe 数据，避免每次 Include
    }

    // 缓存：避免每次分页都做 Include 查询
    private readonly Dictionary<string, string> _projectCache; // code → name
    private readonly Dictionary<string, string> _unitCache;    // code → name
    private readonly Dictionary<int, string> _recipeCache;     // id → recipeName

    private async Task LoadLookupCacheAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var projects = await context.Projects
                .AsNoTracking()
                .Select(p => new { p.Code, p.Name })
                .ToListAsync();
            foreach (var p in projects) _projectCache[p.Code] = p.Name;

            var units = await context.Units
                .AsNoTracking()
                .Select(u => new { u.Code, u.Name, u.ProjectCode })
                .ToListAsync();
            foreach (var u in units) _unitCache[u.Code] = u.Name;

            var recipes = await context.TestRecipes
                .AsNoTracking()
                .Select(r => new { r.Id, r.RecipeName })
                .ToListAsync();
            foreach (var r in recipes) _recipeCache[r.Id] = r.RecipeName;

            // 填充项目筛选选项
            ProjectOptions.Clear();
            ProjectOptions.Add(new ProjectFilterItem(null, "全部项目"));
            foreach (var p in projects)
                ProjectOptions.Add(new ProjectFilterItem(p.Code, p.Name));

            // 按项目分组缓存机组
            _unitsByProject.Clear();
            var grouped = units.GroupBy(u => u.ProjectCode);
            foreach (var g in grouped)
            {
                var list = g.Select(u => new UnitFilterItem(u.Code, u.Name)).ToList();
                _unitsByProject[g.Key] = list;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载项目和机组缓存失败");
        }
    }

    private ObservableCollection<TestRecord> _filteredRecords = [];
    public ObservableCollection<TestRecord> FilteredRecords
    {
        get => _filteredRecords;
        set => SetProperty(ref _filteredRecords, value);
    }

    public IReadOnlyList<string> ResultOptions { get; }

    /// <summary>
    /// 当前页码
    /// </summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(HasPreviousPage));
                OnPropertyChanged(nameof(HasNextPage));
                OnPropertyChanged(nameof(PageStatusText));
            }
        }
    }

    /// <summary>
    /// 每页条数（根据屏幕宽度自动调整：<1920显示8条，>=1920显示10条）
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (SetProperty(ref _pageSize, value))
            {
                // 页数变化后重新查询
                if (QueryCommand.CanExecute(null))
                {
                    QueryCommand.Execute(null);
                }
            }
        }
    }

    /// <summary>
    /// 总页数
    /// </summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    /// <summary>
    /// 是否有上一页
    /// </summary>
    public bool HasPreviousPage => CurrentPage > 1;

    /// <summary>
    /// 是否有下一页
    /// </summary>
    public bool HasNextPage => CurrentPage < TotalPages;

    /// <summary>
    /// 当前页状态文本
    /// </summary>
    public string PageStatusText => TotalPages > 0 ? $"第 {CurrentPage} / {TotalPages} 页" : "无数据";

    /// <summary>
    /// 分页状态文本
    /// </summary>
    public string PaginationStatus => $"第 {CurrentPage} / {TotalPages} 页，共 {TotalCount:N0} 条记录";

    /// <summary>
    /// 跳转页码命令（供 PaginationControl 调用）
    /// </summary>
    public ICommand GoToPageCommand => new Controls.SimpleCommand<int>(ApplyPageNavigation);

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string ResultFilter
    {
        get => _resultFilter;
        set
        {
            if (SetProperty(ref _resultFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    /// <summary>导入时间筛选选项</summary>
    public ObservableCollection<string> ImportTimeFilterOptions { get; } =
    [
        "全部",
        "最近1小时",
        "今天导入",
        "最近3天",
        "最近7天"
    ];

    /// <summary>导入时间筛选</summary>
    public string ImportTimeFilter
    {
        get => _importTimeFilter;
        set
        {
            if (SetProperty(ref _importTimeFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    /// <summary>项目筛选选项</summary>
    public ObservableCollection<ProjectFilterItem> ProjectOptions { get; } = [];

    /// <summary>机组筛选选项（根据选中项目联动）</summary>
    public ObservableCollection<UnitFilterItem> UnitOptions { get; } = [];

    /// <summary>全量机组缓存（projectCode → list）</summary>
    private readonly Dictionary<string, List<UnitFilterItem>> _unitsByProject = new();

    /// <summary>选中的项目编码</summary>
    public string? SelectedProjectCode
    {
        get => _selectedProjectCode;
        set
        {
            if (SetProperty(ref _selectedProjectCode, value))
            {
                // 联动刷新机组下拉
                RefreshUnitOptions();
                ApplyQuery();
            }
        }
    }

    /// <summary>选中的机组编码</summary>
    public string? SelectedUnitCode
    {
        get => _selectedUnitCode;
        set
        {
            if (SetProperty(ref _selectedUnitCode, value))
            {
                ApplyQuery();
            }
        }
    }

    /// <summary>时间范围-起始</summary>
    public DateTime? DateFrom
    {
        get => _dateFrom;
        set
        {
            if (SetProperty(ref _dateFrom, value))
            {
                ApplyQuery();
            }
        }
    }

    /// <summary>时间范围-截止</summary>
    public DateTime? DateTo
    {
        get => _dateTo;
        set
        {
            if (SetProperty(ref _dateTo, value))
            {
                ApplyQuery();
            }
        }
    }

    /// <summary>重置所有筛选条件</summary>
    public ICommand ResetFiltersCommand => new RelayCommand(() =>
    {
        ResultFilter = "全部";
        SearchText = string.Empty;
        SelectedProjectCode = null;
        SelectedUnitCode = null;
        DateFrom = null;
        DateTo = null;
        ImportTimeFilter = "全部";
    });

    private void RefreshUnitOptions()
    {
        UnitOptions.Clear();
        if (_selectedProjectCode != null && _unitsByProject.TryGetValue(_selectedProjectCode, out var units))
        {
            foreach (var u in units) UnitOptions.Add(u);
        }
        SelectedUnitCode = null; // 切换项目时清空机组选择
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _errorDetail = string.Empty;
    /// <summary>
    /// 详细错误信息（用于复制）
    /// </summary>
    public string ErrorDetail
    {
        get => _errorDetail;
        set
        {
            if (SetProperty(ref _errorDetail, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>
    /// 是否有错误信息
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorDetail);

    /// <summary>
    /// 复制错误信息到剪贴板
    /// </summary>
    public ICommand CopyErrorCommand => new RelayCommand(() =>
    {
        if (!string.IsNullOrWhiteSpace(ErrorDetail))
        {
            System.Windows.Clipboard.SetText(ErrorDetail);
            StatusMessage = "✅ 错误信息已复制到剪贴板";
        }
    });

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(HasPreviousPage));
                OnPropertyChanged(nameof(HasNextPage));
                OnPropertyChanged(nameof(PaginationStatus));
                OnPropertyChanged(nameof(PageStatusText));
            }
        }
    }

    /// <summary>
    /// 响应分页控件的跳转请求
    /// </summary>
    private async Task ApplyPageNavigationAsync(int page)
    {
        if (page < 1 || page > TotalPages) return;

        CurrentPage = page;
        await ApplyQueryWithPagination();
    }

    // UI 命令绑定兼容入口
    private async void ApplyPageNavigation(int page) => await ApplyPageNavigationAsync(page);

    public TestRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                // 切换记录时始终重置回放区间，避免沿用上一条记录的裁剪区间
                PlaybackStartTime = 0;
                PlaybackEndTime = 0;

                // 解析配方快照
                ParseRecipeSnapshot(value);

                if (!_suppressChartUpdate)
                    _ = UpdateChartFromSelectedAsync();
            }
        }
    }

    /// <summary>
    /// 解析配方快照JSON
    /// </summary>
    private void ParseRecipeSnapshot(TestRecord? record)
    {
        if (record?.RecipeSnapshotJson == null)
        {
            RecipeLeakageLimit = null;
            RecipePrechargeP2 = null;
            RecipeSystem = null;
            RecipePenetrationDiameter = null;
            RecipeValveNo = null;
            RecipeValveNominalDiameter = null;
            RecipeRemark = null;
            return;
        }

        try
        {
            var snapshot = RecipeService.ParseSnapshot(record.RecipeSnapshotJson);
            if (snapshot != null)
            {
                RecipeLeakageLimit = snapshot.LeakageLimit;
                RecipePrechargeP2 = snapshot.PrechargePressureP2;
                RecipeSystem = string.IsNullOrWhiteSpace(snapshot.System) ? null : snapshot.System;
                RecipePenetrationDiameter = snapshot.PenetrationDiameter;
                RecipeValveNo = string.IsNullOrWhiteSpace(snapshot.ValveNo) ? null : snapshot.ValveNo;
                RecipeValveNominalDiameter = snapshot.ValveNominalDiameter;
                RecipeRemark = string.IsNullOrWhiteSpace(snapshot.Remark) ? null : snapshot.Remark;
            }
            else
            {
                RecipeLeakageLimit = null;
                RecipePrechargeP2 = null;
                RecipeSystem = null;
                RecipePenetrationDiameter = null;
                RecipeValveNo = null;
                RecipeValveNominalDiameter = null;
                RecipeRemark = null;
            }
        }
        catch
        {
            RecipeLeakageLimit = null;
            RecipePrechargeP2 = null;
            RecipeSystem = null;
            RecipePenetrationDiameter = null;
            RecipeValveNo = null;
            RecipeValveNominalDiameter = null;
            RecipeRemark = null;
        }
    }

    // Min/Max 已迁移到 TrendChannel.Min/Max，图例直接绑定通道对象

    /// <summary>
    /// 当前选中记录是否有真实过程曲线数据（无数据时界面显示空状态，不再伪造曲线）
    /// </summary>
    public bool HasCurveData
    {
        get => _hasCurveData;
        private set
        {
            if (SetProperty(ref _hasCurveData, value))
                OnPropertyChanged(nameof(NoCurveData));
        }
    }

    /// <summary>无过程曲线数据（用于空状态提示绑定）</summary>
    public bool NoCurveData => !_hasCurveData;

    public ICommand QueryCommand => new RelayCommand(ApplyQuery);

    /// <summary>双击修改配方命令</summary>
    public ICommand ChangeRecipeCommand => new RelayCommand(
        async () => await ChangeRecipeAsync(),
        () => SelectedRecord != null && PermissionGuard.Can(Perms.RecordsUpload));

    /// <summary>是否有选中的记录（用于批量操作按钮状态）</summary>
    public bool HasSelectedRecords => FilteredRecords.Any(r => r.IsSelected);

    /// <summary>批量修改配方命令</summary>
    public IRelayCommand BatchChangeRecipeCommand => _batchChangeRecipeCommand ??= new RelayCommand(
        async () => await BatchChangeRecipeAsync(),
        () => HasSelectedRecords && PermissionGuard.Can(Perms.RecordsUpload));
    private IRelayCommand? _batchChangeRecipeCommand;

    /// <summary>通知选中状态已改变，刷新命令状态</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedRecords));
        _batchChangeRecipeCommand?.NotifyCanExecuteChanged();
        _deleteSelectedCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>所有可用配方列表</summary>
    public ObservableCollection<TestRecipe> AvailableRecipes { get; } = [];

    /// <summary>跨页保留的选中记录编码集合</summary>
    private readonly HashSet<string> _selectedRecordCodes = [];

    /// <summary>更新选中状态集合（在分页时调用）</summary>
    private void UpdateSelectedRecordCodes()
    {
        // 保存当前页的选中状态
        foreach (var record in FilteredRecords)
        {
            if (record.IsSelected)
                _selectedRecordCodes.Add(record.RecordCode);
            else
                _selectedRecordCodes.Remove(record.RecordCode);
        }
    }

    /// <summary>全选状态</summary>
    private bool _allSelected;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (SetProperty(ref _allSelected, value))
            {
                foreach (var record in FilteredRecords)
                    record.IsSelected = value;
                // 通知选中状态已改变，刷新命令状态
                NotifySelectionChanged();
            }
        }
    }

    /// <summary>切换全选命令</summary>
    public ICommand ToggleAllSelectionCommand => new RelayCommand(() =>
    {
        foreach (var record in FilteredRecords)
            record.IsSelected = AllSelected;
        // 通知选中状态已改变，刷新命令状态
        NotifySelectionChanged();
    });

    /// <summary>双击修改单个记录的配方</summary>
    private async Task ChangeRecipeAsync()
    {
        if (SelectedRecord == null)
            return;

        await ShowRecipeChangeDialogAsync(new List<TestRecord> { SelectedRecord });
    }

    /// <summary>批量修改选中记录的配方</summary>
    private async Task BatchChangeRecipeAsync()
    {
        var selectedRecords = FilteredRecords.Where(r => r.IsSelected).ToList();
        if (!selectedRecords.Any())
            return;

        await ShowRecipeChangeDialogAsync(selectedRecords);
    }

    /// <summary>显示配方修改对话框</summary>
    private async Task ShowRecipeChangeDialogAsync(List<TestRecord> records)
    {
        try
        {
            // 加载可用配方
            if (!AvailableRecipes.Any())
            {
                using var context = DbContextFactory.CreateDbContext();
                var recipes = await context.TestRecipes
                    .Where(r => r.IsEnabled)
                    .OrderBy(r => r.SortOrder)
                    .ToListAsync();

                AvailableRecipes.Clear();
                foreach (var recipe in recipes)
                    AvailableRecipes.Add(recipe);
            }

            if (!AvailableRecipes.Any())
            {
                MessageBox.Show("系统中没有可用的试验路径，请先在试验路径管理中添加试验路径。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 显示对话框
            var dialog = new Views.RecipeChangeDialog
            {
                Owner = Application.Current.MainWindow,
                CurrentRecipeName = records.Count == 1
                    ? (records[0].RecipeName ?? "（无）")
                    : $"（{records.Count} 条记录）",
                AvailableRecipes = AvailableRecipes,
                RecordCount = records.Count
            };

            if (dialog.ShowDialog() != true || dialog.SelectedRecipe == null)
                return;

            // 执行修改
            await UpdateRecipeAsync(records, dialog.SelectedRecipe);
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 修改试验路径失败：{ex.Message}";
            MessageBox.Show($"修改试验路径失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>更新记录的试验路径</summary>
    private async Task UpdateRecipeAsync(List<TestRecord> records, TestRecipe newRecipe)
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在修改试验路径...";

            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            // 获取需要更新的记录
            var recordCodes = records.Select(r => r.RecordCode).ToList();
            var recordsToUpdate = await context.TestRecords
                .Where(r => recordCodes.Contains(r.RecordCode))
                .ToListAsync();

            foreach (var record in recordsToUpdate)
            {
                var oldRecipeName = record.TestRecipeId.HasValue
                    ? (_recipeCache.TryGetValue(record.TestRecipeId.Value, out var name) ? name : "未知")
                    : "（无）";

                record.TestRecipeId = newRecipe.Id;

                // 【关键】创建配方快照（保存修改时的配方参数，不受后续配方修改影响）
                record.RecipeSnapshotJson = await AppServices.RecipeService.CreateSnapshotForTestAsync(newRecipe.Id);

                // 获取配方版本号
                record.RecipeVersionNumber = await AppServices.RecipeService.GetCurrentVersionAsync(newRecipe.Id);

                // 记录操作日志
                await logService.LogAsync("修改试验路径", currentUser,
                    $"试验记录 [{record.RecordCode}] 试验路径从 {oldRecipeName} 修改为 {newRecipe.RecipeName}", "Success");
            }

            await context.SaveChangesAsync();

            // 更新缓存
            _recipeCache[newRecipe.Id] = newRecipe.RecipeName;

            // 刷新列表
            await ApplyQueryWithPagination();

            // 清除选中状态，提供操作完成的视觉反馈
            foreach (var record in FilteredRecords)
                record.IsSelected = false;
            AllSelected = false;
            NotifySelectionChanged();

            StatusMessage = $"✅ 已修改 {recordsToUpdate.Count} 条记录的试验路径为 {newRecipe.RecipeName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 修改试验路径失败：{ex.Message}";
            MessageBox.Show($"修改试验路径失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除选中记录命令</summary>
    public IRelayCommand DeleteSelectedCommand => _deleteSelectedCommand ??= new RelayCommand(
        async () => await DeleteSelectedAsync(),
        () => HasSelectedRecords && PermissionGuard.Can(Perms.RecordsDelete));
    private IRelayCommand? _deleteSelectedCommand;

    /// <summary>删除指定行记录命令（表格操作列使用）</summary>
    public ICommand DeleteRecordCommand => new AsyncRelayCommand<TestRecord>(
        async record => await DeleteRecordAsync(record),
        record => PermissionGuard.Can(Perms.RecordsDelete));

    private async Task DeleteSelectedAsync()
    {
        var selectedRecords = FilteredRecords.Where(r => r.IsSelected).ToList();
        if (selectedRecords.Count == 0)
            return;

        // 确认框
        var result = MessageBox.Show(
            $"确定要删除选中的 {selectedRecords.Count} 条试验记录吗？\n\n此操作不可恢复！",
            "确认批量删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
            return;

        try
        {
            IsLoading = true;
            StatusMessage = $"正在删除 {selectedRecords.Count} 条记录...";

            using var context = DbContextFactory.CreateDbContext();
            using var transaction = await context.Database.BeginTransactionAsync();

            int successCount = 0;
            foreach (var record in selectedRecords)
            {
                // 删除过程数据
                var processData = await context.TestProcessData
                    .FirstOrDefaultAsync(p => p.RecordCode == record.RecordCode);
                if (processData != null)
                    context.TestProcessData.Remove(processData);

                // 删除主记录
                var recordToDelete = await context.TestRecords
                    .FirstOrDefaultAsync(r => r.RecordCode == record.RecordCode);
                if (recordToDelete != null)
                    context.TestRecords.Remove(recordToDelete);

                // 记录操作日志
                try
                {
                    var logService = new OperationLogService(context);
                    var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
                    await logService.LogAsync(
                        "删除试验记录",
                        currentUser,
                        $"删除试验记录 [{record.RecordCode}] - 对象: {record.ObjectCode}, 试验时间: {record.TestTime:yyyy-MM-dd HH:mm:ss}",
                        "Success");
                }
                catch { /* 日志失败不影响删除操作结果 */ }

                successCount++;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            // 清空选中状态
            _selectedRecordCodes.Clear();
            AllSelected = false;

            // 重新加载当前页数据
            await ApplyQueryWithPagination();

            StatusMessage = $"✅ 已删除 {successCount} 条记录，剩余 {TotalCount:N0} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 删除失败：{ex.Message}";
            MessageBox.Show($"删除失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 删除指定的试验记录
    /// </summary>
    private async Task DeleteRecordAsync(TestRecord? record)
    {
        if (record == null)
            return;

        // 1. 确认框
        var result = MessageBox.Show(
            $"确定要删除试验记录 [{record.RecordCode}] 吗？\n\n此操作不可恢复！",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
            return;

        try
        {
            IsLoading = true;
            StatusMessage = "正在删除...";

            using var context = DbContextFactory.CreateDbContext();

            // 2. 删除过程数据（如果有）
            var processData = await context.TestProcessData
                .FirstOrDefaultAsync(p => p.RecordCode == record.RecordCode);

            if (processData != null)
                context.TestProcessData.Remove(processData);

            // 3. 删除主记录
            var recordToDelete = await context.TestRecords
                .FirstOrDefaultAsync(r => r.RecordCode == record.RecordCode);

            if (recordToDelete != null)
                context.TestRecords.Remove(recordToDelete);

            // 4. 记录操作日志
            try
            {
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
                await logService.LogAsync(
                    "删除试验记录",
                    currentUser,
                    $"删除试验记录 [{record.RecordCode}] - 对象: {record.ObjectCode}, 试验时间: {record.TestTime:yyyy-MM-dd HH:mm:ss}",
                    "Success");
            }
            catch
            {
                // 日志失败不影响删除操作结果
            }

            // 5. 提交数据库
            await context.SaveChangesAsync();

            // 6. 重新加载当前页数据
            await ApplyQueryWithPagination();

            StatusMessage = $"✅ 已删除记录，剩余 {TotalCount:N0} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 删除失败：{ex.Message}";
            MessageBox.Show($"删除失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 页面切换时刷新数据
    /// </summary>
    Task IRefreshable.RefreshAsync() => RefreshAllAsync();

    /// <summary>
    /// 页面激活时刷新：既重载项目/机组/配方查找缓存与筛选下拉（否则批量导入新建的
    /// 项目/机组不重启不显示），也刷新记录列表。刷新时尽量保留当前筛选选择。
    /// </summary>
    private async Task RefreshAllAsync()
    {
        var prevProject = _selectedProjectCode;
        var prevUnit = _selectedUnitCode;

        await LoadLookupCacheAsync();

        // 下拉重建后按编码恢复筛选选择（直接改字段避免触发 ApplyQuery，末尾统一 LoadDataAsync）
        if (prevProject != null && _projectCache.ContainsKey(prevProject))
        {
            _selectedProjectCode = prevProject;
            OnPropertyChanged(nameof(SelectedProjectCode));
            RefreshUnitOptions();
            if (prevUnit != null && _unitCache.ContainsKey(prevUnit))
            {
                _selectedUnitCode = prevUnit;
                OnPropertyChanged(nameof(SelectedUnitCode));
            }
        }

        await LoadDataAsync();
    }

    /// <summary>
    /// 初始加载数据
    /// </summary>
    private async Task LoadDataAsync()
    {
        var sw = Stopwatch.StartNew();
        var perfLog = new List<string>();
        void LogPerf(string msg) { perfLog.Add(msg); Debug.WriteLine(msg); }
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载...";

            var sw1 = Stopwatch.StartNew();
            var (records, count) = await LoadPageDataAsync();
            sw1.Stop();
            LogPerf($"① 单SQL查询({records.Count}条): {sw1.ElapsedMilliseconds}ms");
            LogPerf($"【总计】{sw1.ElapsedMilliseconds}ms");

            TotalCount = count;
            ReplaceRecords(records);
            SelectedRecord = FilteredRecords.FirstOrDefault();
            StatusMessage = PaginationStatus;

            WritePerfLog(perfLog);
        }
        catch (Exception ex)
        {
            var msg = $"加载失败：{ex.Message}";
            StatusMessage = msg;
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            WriteLog($"ERROR in LoadDataAsync: {ex}");
            WritePerfLog(perfLog);
        }
        finally
        {
            IsLoading = false;
            sw.Stop();
            Debug.WriteLine($"[分页性能] 总计: {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// 应用查询过滤（带分页）
    /// </summary>
    private async Task ApplyQueryWithPagination()
    {
        var wallStart = DateTime.Now;
        var sw = Stopwatch.StartNew();
        var perfLog = new List<string>();
        void LogPerf(string msg) { perfLog.Add(msg); Debug.WriteLine(msg); }
        try
        {
            IsLoading = true;

            // 保存当前页的选中状态到跨页集合
            UpdateSelectedRecordCodes();

            var sw1 = Stopwatch.StartNew();
            var (records, count) = await LoadPageDataAsync();
            sw1.Stop();
            LogPerf($"① 单SQL查询({records.Count}条): {sw1.ElapsedMilliseconds}ms");

            TotalCount = count;

            if (CurrentPage > TotalPages)
                CurrentPage = TotalPages > 0 ? TotalPages : 1;

            var sw3 = Stopwatch.StartNew();
            // 分页时不触发曲线更新，避免额外数据库查询
            _suppressChartUpdate = true;
            _selectedRecord = null;

            // 恢复跨页选中的记录
            foreach (var record in records)
            {
                record.IsSelected = _selectedRecordCodes.Contains(record.RecordCode);
            }

            ReplaceRecords(records);
            // 默认选中第一条记录（触发属性变更通知）
            SelectedRecord = FilteredRecords.FirstOrDefault(r => r.IsSelected) ?? FilteredRecords.FirstOrDefault();
            _suppressChartUpdate = false;
            StatusMessage = PaginationStatus;
            sw3.Stop();
            LogPerf($"③ ReplaceRecords+Selected: {sw3.ElapsedMilliseconds}ms");

            IsLoading = false;
            sw.Stop();
            var wallMs = (int)(DateTime.Now - wallStart).TotalMilliseconds;
            LogPerf($"⑤ 代码总计: {sw.ElapsedMilliseconds}ms");
            LogPerf($"⑥ 实际耗时(含UI渲染): {wallMs}ms");
            WritePerfLog(perfLog);
        }
        catch (Exception ex)
        {
            _suppressChartUpdate = false;
            IsLoading = false;
            StatusMessage = $"查询失败：{ex.Message}";
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            WriteLog($"ERROR in ApplyQueryWithPagination: {ex}");
        }
    }

    /// <summary>
    /// 应用查询过滤
    /// </summary>
    public async Task ApplyQueryAsync()
    {
        // 查询时回到第一页
        CurrentPage = 1;
        await ApplyQueryWithPagination();
    }

    // UI 命令绑定兼容入口
    public async void ApplyQuery() => await ApplyQueryAsync();

    /// <summary>
    /// 用一条 SQL 返回分页数据（含总数），只开一次连接，彻底避免连接池延迟
    /// </summary>
    private async Task<(List<TestRecord> Records, int TotalCount)> LoadPageDataAsync()
    {
        var records = new List<TestRecord>();
        int totalCount = 0;

        var swConnect = Stopwatch.StartNew();
        var connectionString = DbContextFactory.GetDefaultConnectionString();
        using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        swConnect.Stop();

        var whereClauses = new List<string>();
        var parameters = new List<Microsoft.Data.SqlClient.SqlParameter>();

        if (!string.IsNullOrEmpty(ResultFilter) && ResultFilter != "全部")
        {
            // TestResult 枚举：未知=0，合格=1，不合格=2
            var resultValue = ResultFilter switch
            {
                "合格" => 1,
                "不合格" => 2,
                "未知" => 0,
                _ => -1
            };
            whereClauses.Add("r.Result = @rv");
            parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@rv", resultValue));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var kw = "%" + SearchText + "%";
            whereClauses.Add("(r.RecordCode LIKE @k1 OR r.ObjectCode LIKE @k2 OR r.ObjectName LIKE @k3 OR r.DeviceCode LIKE @k4 OR r.DataPackageName LIKE @k5)");
            for (int i = 1; i <= 5; i++) parameters.Add(new Microsoft.Data.SqlClient.SqlParameter($"@k{i}", kw));
        }

        if (!string.IsNullOrWhiteSpace(SelectedProjectCode))
        {
            whereClauses.Add("r.ProjectCode = @pc");
            parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@pc", SelectedProjectCode));
        }

        if (!string.IsNullOrWhiteSpace(SelectedUnitCode))
        {
            whereClauses.Add("r.UnitCode = @uc");
            parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@uc", SelectedUnitCode));
        }

        if (DateFrom.HasValue)
        {
            whereClauses.Add("r.TestTime >= @df");
            parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@df", DateFrom.Value.Date));
        }

        if (DateTo.HasValue)
        {
            whereClauses.Add("r.TestTime <= @dt");
            parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@dt", DateTo.Value.Date.AddDays(1).AddSeconds(-1)));
        }

        // 导入时间筛选
        if (!string.IsNullOrEmpty(ImportTimeFilter) && ImportTimeFilter != "全部")
        {
            var now = DateTime.Now;
            DateTime importFrom;

            switch (ImportTimeFilter)
            {
                case "最近1小时":
                    importFrom = now.AddHours(-1);
                    break;
                case "今天导入":
                    importFrom = now.Date;
                    break;
                case "最近3天":
                    importFrom = now.AddDays(-3);
                    break;
                case "最近7天":
                    importFrom = now.AddDays(-7);
                    break;
                default:
                    importFrom = DateTime.MinValue;
                    break;
            }

            if (importFrom > DateTime.MinValue)
            {
                whereClauses.Add("r.ImportTime >= @importFrom");
                parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@importFrom", importFrom));
            }
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        var offset = (CurrentPage - 1) * PageSize;

        var sql = $@"
            WITH CTE AS (
                SELECT r.RecordCode, r.ProjectCode, r.UnitCode, r.ObjectCode, r.ObjectName,
                       r.ObjectType, r.DeviceCode, r.DataPackageName, r.TestTime, r.ImportTime,
                       r.Operator, r.TestPressure, r.LeakageLimit, r.FinalLeakageRate, r.Result,
                       r.Remark, r.StepSummary, r.ResultFieldSummary, r.ProcessChannelSummary, r.CreatedAt,
                       r.TestRecipeId, r.RecipeSnapshotJson, r.RecipeVersionNumber,
                       ROW_NUMBER() OVER (ORDER BY r.TestTime DESC) AS RowNum,
                       COUNT(*) OVER() AS TotalCount
                FROM TestRecords r
                {whereSql}
            )
            SELECT * FROM CTE
            WHERE RowNum BETWEEN @offset + 1 AND @offset + @pageSize";

        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.AddWithValue("@offset", offset);
        cmd.Parameters.AddWithValue("@pageSize", PageSize);

        var swRead = Stopwatch.StartNew();
        using var reader = await cmd.ExecuteReaderAsync();
        swRead.Stop();

        while (await reader.ReadAsync())
        {
            if (totalCount == 0)
                totalCount = reader.GetInt32(reader.GetOrdinal("TotalCount"));

            var record = new TestRecord
            {
                RecordCode = reader.GetString(reader.GetOrdinal("RecordCode")),
                ProjectCode = reader.GetString(reader.GetOrdinal("ProjectCode")),
                UnitCode = reader.GetString(reader.GetOrdinal("UnitCode")),
                ObjectCode = reader.GetString(reader.GetOrdinal("ObjectCode")),
                ObjectName = reader.IsDBNull(reader.GetOrdinal("ObjectName")) ? null : reader.GetString(reader.GetOrdinal("ObjectName")),
                ObjectType = (PathNodeType)reader.GetInt32(reader.GetOrdinal("ObjectType")),
                DeviceCode = reader.GetString(reader.GetOrdinal("DeviceCode")),
                DataPackageName = reader.IsDBNull(reader.GetOrdinal("DataPackageName")) ? null : reader.GetString(reader.GetOrdinal("DataPackageName")),
                TestTime = reader.GetDateTime(reader.GetOrdinal("TestTime")),
                ImportTime = reader.GetDateTime(reader.GetOrdinal("ImportTime")),
                Operator = reader.GetString(reader.GetOrdinal("Operator")),
                TestPressure = reader.GetDecimal(reader.GetOrdinal("TestPressure")),
                LeakageLimit = reader.GetDecimal(reader.GetOrdinal("LeakageLimit")),
                FinalLeakageRate = reader.GetDecimal(reader.GetOrdinal("FinalLeakageRate")),
                Result = (TestResult)reader.GetInt32(reader.GetOrdinal("Result")),
                Remark = reader.IsDBNull(reader.GetOrdinal("Remark")) ? null : reader.GetString(reader.GetOrdinal("Remark")),
                StepSummary = reader.IsDBNull(reader.GetOrdinal("StepSummary")) ? null : reader.GetString(reader.GetOrdinal("StepSummary")),
                ResultFieldSummary = reader.IsDBNull(reader.GetOrdinal("ResultFieldSummary")) ? null : reader.GetString(reader.GetOrdinal("ResultFieldSummary")),
                ProcessChannelSummary = reader.IsDBNull(reader.GetOrdinal("ProcessChannelSummary")) ? null : reader.GetString(reader.GetOrdinal("ProcessChannelSummary")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                TestRecipeId = reader.IsDBNull(reader.GetOrdinal("TestRecipeId")) ? null : reader.GetInt32(reader.GetOrdinal("TestRecipeId")),
                RecipeSnapshotJson = reader.IsDBNull(reader.GetOrdinal("RecipeSnapshotJson")) ? null : reader.GetString(reader.GetOrdinal("RecipeSnapshotJson")),
                RecipeVersionNumber = reader.IsDBNull(reader.GetOrdinal("RecipeVersionNumber")) ? null : reader.GetInt32(reader.GetOrdinal("RecipeVersionNumber")),
            };

            if (_projectCache.TryGetValue(record.ProjectCode, out var pname))
                record.Project = new Project { Code = record.ProjectCode, Name = pname };
            if (_unitCache.TryGetValue(record.UnitCode, out var uname))
                record.Unit = new Unit { Code = record.UnitCode, Name = uname };
            if (record.TestRecipeId.HasValue && _recipeCache.TryGetValue(record.TestRecipeId.Value, out var rname))
                record.TestRecipe = new TestRecipe { Id = record.TestRecipeId.Value, RecipeName = rname };

            records.Add(record);
        }

        Debug.WriteLine($"  [SQL细节] 连接打开: {swConnect.ElapsedMilliseconds}ms, 执行查询: {swRead.ElapsedMilliseconds}ms");
        WriteLog($"  [SQL细节] 连接打开: {swConnect.ElapsedMilliseconds}ms, 执行查询: {swRead.ElapsedMilliseconds}ms");

        return (records, totalCount);
    }

    /// <summary>
    /// 用 ADO.NET 直接加载记录数据，彻底避免 EF 偶发 3 秒延迟
    /// </summary>
    private async Task<List<TestRecord>> LoadRecordsByIdsAsync(List<string> recordCodes)
    {
        var records = new List<TestRecord>();
        var connectionString = DbContextFactory.GetDefaultConnectionString();

        using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        var inParams = string.Join(",", recordCodes.Select((_, i) => $"@p{i}"));
        var sql = $@"SELECT RecordCode, ProjectCode, UnitCode, ObjectCode, ObjectName,
                ObjectType, DeviceCode, DataPackageName, TestTime, ImportTime,
                Operator, TestPressure, LeakageLimit, FinalLeakageRate, Result,
                Remark, StepSummary, ResultFieldSummary, ProcessChannelSummary, CreatedAt,
                TestRecipeId, RecipeSnapshotJson, RecipeVersionNumber
            FROM TestRecords WHERE RecordCode IN ({inParams}) ORDER BY TestTime DESC";

        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        for (int i = 0; i < recordCodes.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", recordCodes[i]);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var record = new TestRecord
            {
                RecordCode = reader.GetString(0),
                ProjectCode = reader.GetString(1),
                UnitCode = reader.GetString(2),
                ObjectCode = reader.GetString(3),
                ObjectName = reader.IsDBNull(4) ? null : reader.GetString(4),
                ObjectType = (PathNodeType)reader.GetInt32(5),
                DeviceCode = reader.GetString(6),
                DataPackageName = reader.IsDBNull(7) ? null : reader.GetString(7),
                TestTime = reader.GetDateTime(8),
                ImportTime = reader.GetDateTime(9),
                Operator = reader.GetString(10),
                TestPressure = reader.GetDecimal(11),
                LeakageLimit = reader.GetDecimal(12),
                FinalLeakageRate = reader.GetDecimal(13),
                Result = (TestResult)reader.GetInt32(14),
                Remark = reader.IsDBNull(15) ? null : reader.GetString(15),
                StepSummary = reader.IsDBNull(16) ? null : reader.GetString(16),
                ResultFieldSummary = reader.IsDBNull(17) ? null : reader.GetString(17),
                ProcessChannelSummary = reader.IsDBNull(18) ? null : reader.GetString(18),
                CreatedAt = reader.GetDateTime(19),
                TestRecipeId = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                RecipeSnapshotJson = reader.IsDBNull(21) ? null : reader.GetString(21),
                RecipeVersionNumber = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            };

            if (_projectCache.TryGetValue(record.ProjectCode, out var pname))
                record.Project = new Project { Code = record.ProjectCode, Name = pname };
            if (_unitCache.TryGetValue(record.UnitCode, out var uname))
                record.Unit = new Unit { Code = record.UnitCode, Name = uname };
            if (record.TestRecipeId.HasValue && _recipeCache.TryGetValue(record.TestRecipeId.Value, out var rname))
                record.TestRecipe = new TestRecipe { Id = record.TestRecipeId.Value, RecipeName = rname };

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// 批量替换记录列表（用新集合替换旧集合，只触发一次 PropertyChanged）
    /// </summary>
    private void ReplaceRecords(List<TestRecord> newRecords)
    {
        var startIndex = (CurrentPage - 1) * PageSize;
        for (int i = 0; i < newRecords.Count; i++)
            newRecords[i].RowNumber = startIndex + i + 1;

        // 取消订阅旧集合的事件
        foreach (var record in _filteredRecords)
        {
            record.PropertyChanged -= OnRecordPropertyChanged;
        }

        // 用新 ObservableCollection 替换旧的，只触发一次 PropertyChanged
        var oldRecords = FilteredRecords;
        _filteredRecords = new ObservableCollection<TestRecord>(newRecords);
        OnPropertyChanged(nameof(FilteredRecords));

        // 订阅新集合的事件
        foreach (var record in _filteredRecords)
        {
            record.PropertyChanged += OnRecordPropertyChanged;
        }

        // 清理旧集合
        oldRecords.Clear();
    }

    /// <summary>
    /// 记录属性变化处理（用于监听 IsSelected 变化）
    /// </summary>
    private void OnRecordPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestRecord.IsSelected))
        {
            OnPropertyChanged(nameof(HasSelectedRecords));
            _deleteSelectedCommand?.NotifyCanExecuteChanged();
            _batchChangeRecipeCommand?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 更新选中记录的曲线数据（带 LRU 缓存淘汰策略，防止长时间运行内存耗尽）。
    /// ️ 仅限 UI 线程调用（SelectedRecord setter 触发，WPF 单线程调度保证安全）。
    /// 无真实过程数据时显示空状态，不再伪造曲线（数据可信度要求）。
    /// </summary>
    private async Task UpdateChartFromSelectedAsync()
    {
        if (SelectedRecord == null)
        {
            ClearCurves();
            return;
        }

        // 先检查缓存（LRU：命中时移到链表头）
        if (_curveCache.TryGetValue(SelectedRecord.RecordCode, out var cachedNode))
        {
            _curveCacheList.Remove(cachedNode);
            _curveCacheList.AddFirst(cachedNode);
            ApplyCurveData(cachedNode.Value.Data);
            return;
        }

        // 显示加载提示（禁止用户操作）
        IsLoadingCurve = true;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var processData = await context.TestProcessData
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.RecordCode == SelectedRecord.RecordCode);

            if (processData != null)
            {
                // LRU 淘汰：超过 MaxCacheSize 时移除最久未使用的
                if (_curveCache.Count >= MaxCacheSize)
                {
                    var lastNode = _curveCacheList.Last;
                    if (lastNode != null)
                    {
                        _curveCacheList.RemoveLast();
                        _curveCache.Remove(lastNode.Value.RecordCode);
                    }
                }

                // 添加到缓存（链表头）
                var newNode = _curveCacheList.AddFirst((SelectedRecord.RecordCode, processData));
                _curveCache[SelectedRecord.RecordCode] = newNode;

                ApplyCurveData(processData);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载试验曲线数据失败");
        }
        finally
        {
            // 隐藏加载提示
            IsLoadingCurve = false;
        }

        // 没有真实过程数据：清空曲线并显示空状态（不伪造数据）
        ClearCurves();
    }

    /// <summary>清空所有曲线并标记无数据。</summary>
    private void ClearCurves()
    {
        DynamicChannels.Clear();
        PressureChannels.Clear();
        TempChannels.Clear();
        FlowChannels.Clear();
        TimeAxisPoints.Clear();
        PlaybackStartTime = 0;
        PlaybackEndTime = 0;
        HasCurveData = false;
    }

    /// <summary>
    /// 应用曲线数据（动态通道）。
    /// 优先从 ChannelsJson 读取；旧记录（ChannelsJson == null）从旧列自动重建。
    /// 数据量大时显示加载提示，禁止用户操作。
    /// </summary>
    private void ApplyCurveData(TestProcessData data)
    {
        // 检查数据量，如果超过阈值则显示加载提示
        var timeData = System.Text.Json.JsonSerializer.Deserialize<double[]>(data.TimeAxisJson ?? "[]") ?? [];
        bool showLoading = timeData.Length > 1000;  // 超过 1000 个数据点显示加载提示

        if (showLoading)
            IsLoadingCurve = true;

        // 使用 Dispatcher 确保 UI 及时更新
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            TimeAxisPoints.ReplaceAll(timeData);

            // 构建通道字典
            Dictionary<string, ChannelData>? channelsDict = null;

            if (!string.IsNullOrEmpty(data.ChannelsJson))
            {
                // 新格式：直接从 ChannelsJson 读取
                try
                {
                    channelsDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ChannelData>>(data.ChannelsJson);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "反序列化 ChannelsJson 失败，回退到旧列");
                }
            }

            if (channelsDict == null || channelsDict.Count == 0)
            {
                // 旧格式：从旧列重建
                channelsDict = BuildChannelsFromLegacyColumns(data);
            }

            // 构建动态通道（分组到压力/温度/流量三个图表）
            DynamicChannels.Clear();
            PressureChannels.Clear();
            TempChannels.Clear();
            FlowChannels.Clear();
            var palette = new[]
            {
                System.Windows.Media.Color.FromRgb(0x07, 0x58, 0xD8), // 蓝
                System.Windows.Media.Color.FromRgb(0x12, 0xA3, 0x66), // 绿
                System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16), // 橙
                System.Windows.Media.Color.FromRgb(0x0E, 0xA5, 0xE9), // 青
                System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6), // 紫
                System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48), // 红
                System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04), // 金
                System.Windows.Media.Color.FromRgb(0x0D, 0x94, 0x88), // 蓝绿
            };

            int idx = 0;
            bool anyData = false;
            int maxChannelPoints = 0;

            foreach (var (key, chData) in channelsDict)
            {
                if (chData.Data == null || chData.Data.Length == 0) continue;
                anyData = true;

                if (chData.Data.Length > maxChannelPoints)
                    maxChannelPoints = chData.Data.Length;

                var channelName = string.IsNullOrEmpty(chData.Name) ? key : chData.Name;
                var channel = new Controls.TrendChannel
                {
                    Name = channelName,
                    Unit = chData.Unit ?? "",
                    Color = palette[idx % palette.Length],
                    Min = chData.Min,
                    Max = chData.Max,
                };
                foreach (var v in chData.Data)
                    channel.Points.Add(v);

                DynamicChannels.Add(channel);       // 保留全量（图例用）
                GroupCollectionFor(channelName).Add(channel);  // 分组到三个图表
                idx++;
            }

            HasCurveData = anyData;

            // 展开重复时间点：同一时间点有多条数据时，将时间段均匀分布
            ExpandDuplicateTimePoints();

            // 备份原始全量数据（用于恢复）
            _originalTimeAxis = new List<double>(TimeAxisPoints);
            _originalChannelData.Clear();
            foreach (var ch in DynamicChannels)
            {
                _originalChannelData[ch.Name] = new List<double>(ch.Points);
            }

            // 检查 TimeAxisPoints 和通道数据点数是否一致
            int targetPoints = TimeAxisPoints.Count;
        if (anyData && targetPoints != maxChannelPoints)
        {
            Log.Warning("[试验记录] 时间轴点数({TimeCount})与通道数据点数({ChannelCount})不一致，以时间轴为准进行对齐",
                TimeAxisPoints.Count, maxChannelPoints);

            // 用 TimeAxisPoints 的点数作为基准，对齐所有通道数据
            foreach (var ch in DynamicChannels)
            {
                if (ch.Points.Count < targetPoints)
                {
                    // 通道数据不足：用最后一个值填充（用 ReplaceAll 触发一次 Reset 事件）
                    var lastValue = ch.Points.Count > 0 ? ch.Points[ch.Points.Count - 1] : 0;
                    var filled = new List<double>(targetPoints);
                    for (int i = 0; i < ch.Points.Count; i++)
                        filled.Add(ch.Points[i]);
                    for (int i = ch.Points.Count; i < targetPoints; i++)
                        filled.Add(lastValue);
                    ch.Points.ReplaceAll(filled);
                    Log.Information("[试验记录] 通道 {Name} 从{Old}点填充到{New}点", ch.Name, ch.Points.Count, targetPoints);
                }
                else if (ch.Points.Count > targetPoints)
                {
                    // 通道数据过多：截断（用 ReplaceAll 触发一次 Reset 事件）
                    var truncated = new List<double>(targetPoints);
                    for (int i = 0; i < targetPoints; i++)
                        truncated.Add(ch.Points[i]);
                    ch.Points.ReplaceAll(truncated);
                    Log.Information("[试验记录] 通道 {Name} 从{Old}点截断到{New}点", ch.Name, ch.Points.Count, targetPoints);
                }
            }
        }

        // 设置回放时间区间（默认为全部）
        PlaybackStartTime = 0;
        PlaybackEndTime = TimeAxisPoints.Count > 0 ? Math.Round(TimeAxisPoints[TimeAxisPoints.Count - 1], 1) : 0;

        // 隐藏加载提示
        if (showLoading)
            IsLoadingCurve = false;
    });
    }

    /// <summary>
    /// 展开重复时间点：同一时间点有多条数据时，将时间段均匀分布，避免图表出现垂直线。
    /// 例如：[0, 1, 2, 2, 2, 2, 2, 3, 4] → [0, 1, 1.4, 1.8, 2.2, 2.6, 3.0, 3, 4]
    /// </summary>
    private void ExpandDuplicateTimePoints()
    {
        if (TimeAxisPoints.Count < 2) return;

        var timeList = TimeAxisPoints.ToList();
        bool hasDuplicates = false;

        // 检查是否有重复时间点
        for (int n = 1; n < timeList.Count; n++)
        {
            if (Math.Abs(timeList[n] - timeList[n - 1]) < 0.0001)
            {
                hasDuplicates = true;
                break;
            }
        }

        if (!hasDuplicates) return;

        Log.Information("[试验记录] 检测到重复时间点，开始展开处理");

        var result = new List<double>(timeList.Count);
        int idx = 0;

        while (idx < timeList.Count)
        {
            // 找到当前时间点的重复区间 [idx, j)
            int j = idx + 1;
            while (j < timeList.Count && Math.Abs(timeList[j] - timeList[idx]) < 0.0001)
                j++;

            int dupCount = j - idx;

            if (dupCount == 1)
            {
                // 无重复，直接添加
                result.Add(timeList[idx]);
            }
            else
            {
                // 有重复，计算时间段宽度并均匀分布
                // 找前一个不同时间点
                double prevTime = idx > 0 ? timeList[idx - 1] : timeList[idx] - 1;
                // 找后一个不同时间点
                double nextTime = j < timeList.Count ? timeList[j] : timeList[idx] + 1;

                // 时间段宽度
                double span = nextTime - prevTime;
                // 每个点的间隔
                double step = span / dupCount;

                // 均匀分布：从 prevTime + step 开始，到 nextTime - step 结束
                for (int k = 0; k < dupCount; k++)
                {
                    result.Add(prevTime + step * (k + 1));
                }

                Log.Information("[试验记录] 展开时间点 {Time} 的 {Count} 条数据，间隔 {Step:F3} 秒", timeList[idx], dupCount, step);
            }

            idx = j;
        }

        TimeAxisPoints.ReplaceAll(result);
    }

    /// <summary>
    /// 从旧列（PressureCurveJson 等）重建 ChannelData 字典。
    /// 用于向后兼容旧记录（ChannelsJson 为 null 的情况）。
    /// </summary>
    private static Dictionary<string, ChannelData> BuildChannelsFromLegacyColumns(TestProcessData data)
    {
        var dict = new Dictionary<string, ChannelData>();

        AddIfPresent("Pressure", "压力P1", "MPa", data.PressureCurveJson, (double)data.PressureMin, (double)data.PressureMax);
        AddIfPresent("Flow", "流量M1", "L/h", data.FlowCurveJson, (double)data.FlowMin, (double)data.FlowMax);
        AddIfPresent("Temp", "温度T", "℃", data.TempCurveJson, (double)data.TempMin, (double)data.TempMax);
        AddIfPresent("Flow2", "流量M2", "L/h", data.Flow2CurveJson, (double)data.Flow2Min, (double)data.Flow2Max);
        AddIfPresent("Pressure2", "压力P2", "MPa", data.Pressure2CurveJson, (double)data.Pressure2Min, (double)data.Pressure2Max);

        return dict;

        void AddIfPresent(string key, string name, string unit, string? json, double min, double max)
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<double[]>(json ?? "[]") ?? [];
            if (arr.Length == 0) return;
            dict[key] = new ChannelData { Name = name, Unit = unit, Data = arr, Min = min, Max = max };
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"testrecords-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "写入试验记录日志失败");
        }
    }

    private static void WritePerfLog(List<string> messages)
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            var logFile = Path.Combine(logDir, $"perf-{DateTime.Now:yyyyMMdd}.log");
            var lines = string.Join("\r\n", messages.Select(m => $"  {m}"));
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] 分页性能:\r\n{lines}\r\n\r\n");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "写入性能日志失败");
        }
    }

    #region IDisposable

    private bool _disposed;

    /// <summary>
    /// 释放资源（MainViewModel.Dispose 时调用）
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 清理 LRU 缓存
        _curveCacheList.Clear();
        _curveCache.Clear();

        // 清理其他缓存
        _projectCache.Clear();
        _unitCache.Clear();
        _recipeCache.Clear();

        Log.Debug("[TestRecordsViewModel] 资源已释放");
    }

    #endregion
}

/// <summary>项目筛选下拉项</summary>
public sealed class ProjectFilterItem
{
    public string? Code { get; }
    public string DisplayName { get; }
    public ProjectFilterItem(string? code, string displayName) { Code = code; DisplayName = displayName; }
    public override string ToString() => DisplayName;
}

/// <summary>机组筛选下拉项</summary>
public sealed class UnitFilterItem
{
    public string Code { get; }
    public string DisplayName { get; }
    public UnitFilterItem(string code, string displayName) { Code = code; DisplayName = displayName; }
    public override string ToString() => DisplayName;
}
