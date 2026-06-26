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
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验记录视图模型（简化版 - 只负责记录查询和详情展示）
/// </summary>
public sealed partial class TestRecordsViewModel : ViewModelBase, IRefreshable
{
    private TestRecord? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "全部";
    private string? _selectedProjectCode;
    private string? _selectedUnitCode;
    private DateTime? _dateFrom;
    private DateTime? _dateTo;
    private bool _isLoading;
    private bool _suppressChartUpdate;
    private string _statusMessage = "加载中...";
    private int _totalCount;
    private int _currentPage = 1;
    private int _pageSize = 10;

    // 曲线数据
    private ObservableCollection<double> _pressureCurvePoints = [];
    private ObservableCollection<double> _flowCurvePoints = [];
    private ObservableCollection<double> _tempCurvePoints = [];
    private readonly Dictionary<string, TestProcessData> _curveCache = new();

    public ObservableCollection<double> PressureCurvePoints
    {
        get => _pressureCurvePoints;
        private set => SetProperty(ref _pressureCurvePoints, value);
    }
    public ObservableCollection<double> FlowCurvePoints
    {
        get => _flowCurvePoints;
        private set => SetProperty(ref _flowCurvePoints, value);
    }
    public ObservableCollection<double> TempCurvePoints
    {
        get => _tempCurvePoints;
        private set => SetProperty(ref _tempCurvePoints, value);
    }

    // 曲线范围
    private double _pressureMin;
    private double _pressureMax;
    private double _flowMin;
    private double _flowMax;
    private double _tempMin;
    private double _tempMax;

    public TestRecordsViewModel()
    {
        ResultOptions = ["全部", "合格", "不合格"];
        _filteredRecords = [];
        _projectCache = new();
        _unitCache = new();
        _ = LoadDataAsync();
        _ = LoadLookupCacheAsync(); // 异步缓存 Project/Unit 数据，避免每次 Include
    }

    // 缓存：避免每次分页都做 Include 查询
    private readonly Dictionary<string, string> _projectCache; // code → name
    private readonly Dictionary<string, string> _unitCache;    // code → name

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
    /// 每页条数
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        private set => SetProperty(ref _pageSize, value);
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
                if (!_suppressChartUpdate)
                    _ = UpdateChartFromSelectedAsync();
            }
        }
    }

    // 曲线范围属性
    public double PressureMin
    {
        get => _pressureMin;
        set => SetProperty(ref _pressureMin, value);
    }

    public double PressureMax
    {
        get => _pressureMax;
        set => SetProperty(ref _pressureMax, value);
    }

    public double FlowMin
    {
        get => _flowMin;
        set => SetProperty(ref _flowMin, value);
    }

    public double FlowMax
    {
        get => _flowMax;
        set => SetProperty(ref _flowMax, value);
    }

    public double TempMin
    {
        get => _tempMin;
        set => SetProperty(ref _tempMin, value);
    }

    public double TempMax
    {
        get => _tempMax;
        set => SetProperty(ref _tempMax, value);
    }

    public ICommand QueryCommand => new RelayCommand(ApplyQuery);

    /// <summary>删除选中记录命令（旧，保留兼容）</summary>
    public ICommand DeleteSelectedCommand => new RelayCommand(
        async () => await DeleteSelectedAsync(),
        () => Services.Security.UserSession.HasPermission("records:data:upload"));

    /// <summary>删除指定行记录命令（表格操作列使用）</summary>
    public ICommand DeleteRecordCommand => new AsyncRelayCommand<TestRecord>(
        async record => await DeleteRecordAsync(record),
        record => Services.Security.UserSession.HasPermission("records:data:upload"));

    private async Task DeleteSelectedAsync()
    {
        if (SelectedRecord == null)
            return;
        await DeleteRecordAsync(SelectedRecord);
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
                    $"删除试验记录 [{record.RecordCode}] - 对象: {record.ObjectCode}, 试验时间: {record.TestTime:yyyy-MM-dd HH:mm}",
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
    Task IRefreshable.RefreshAsync() => LoadDataAsync();

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
            ReplaceRecords(records);
            _selectedRecord = FilteredRecords.FirstOrDefault();
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
            var resultValue = ResultFilter == "合格" ? 1 : 2;
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

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        var offset = (CurrentPage - 1) * PageSize;

        var sql = $@"
            WITH CTE AS (
                SELECT r.RecordCode, r.ProjectCode, r.UnitCode, r.ObjectCode, r.ObjectName,
                       r.ObjectType, r.DeviceCode, r.DataPackageName, r.TestTime, r.ImportTime,
                       r.Operator, r.TestPressure, r.LeakageLimit, r.FinalLeakageRate, r.Result,
                       r.Remark, r.StepSummary, r.ResultFieldSummary, r.ProcessChannelSummary, r.CreatedAt,
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
            };

            if (_projectCache.TryGetValue(record.ProjectCode, out var pname))
                record.Project = new Project { Code = record.ProjectCode, Name = pname };
            if (_unitCache.TryGetValue(record.UnitCode, out var uname))
                record.Unit = new Unit { Code = record.UnitCode, Name = uname };

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
                Remark, StepSummary, ResultFieldSummary, ProcessChannelSummary, CreatedAt
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
            };

            if (_projectCache.TryGetValue(record.ProjectCode, out var pname))
                record.Project = new Project { Code = record.ProjectCode, Name = pname };
            if (_unitCache.TryGetValue(record.UnitCode, out var uname))
                record.Unit = new Unit { Code = record.UnitCode, Name = uname };

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

        // 用新 ObservableCollection 替换旧的，只触发一次 PropertyChanged
        var oldRecords = FilteredRecords;
        _filteredRecords = new ObservableCollection<TestRecord>(newRecords);
        OnPropertyChanged(nameof(FilteredRecords));
        // 清理旧集合的事件订阅
        oldRecords.Clear();
    }

    /// <summary>
    /// 更新选中记录的曲线数据（带缓存，批量替换集合）
    /// </summary>
    private async Task UpdateChartFromSelectedAsync()
    {
        if (SelectedRecord == null)
        {
            PressureCurvePoints = [];
            FlowCurvePoints = [];
            TempCurvePoints = [];
            PressureMin = 0; PressureMax = 1;
            FlowMin = 0; FlowMax = 0.01;
            TempMin = 20; TempMax = 30;
            return;
        }

        // 先检查缓存
        if (_curveCache.TryGetValue(SelectedRecord.RecordCode, out var cached))
        {
            ApplyCurveData(cached);
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var processData = await context.TestProcessData
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.RecordCode == SelectedRecord.RecordCode);

            if (processData != null)
            {
                _curveCache[SelectedRecord.RecordCode] = processData;
                ApplyCurveData(processData);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载试验曲线数据失败，将使用模拟数据");
        }

        // 没有真实数据时生成模拟数据
        GenerateSampleCurve();
    }

    /// <summary>
    /// 批量应用曲线数据（一次替换整个集合）
    /// </summary>
    private void ApplyCurveData(TestProcessData data)
    {
        var pressureData = System.Text.Json.JsonSerializer.Deserialize<double[]>(data.PressureCurveJson ?? "[]") ?? [];
        var flowData = System.Text.Json.JsonSerializer.Deserialize<double[]>(data.FlowCurveJson ?? "[]") ?? [];
        var tempData = System.Text.Json.JsonSerializer.Deserialize<double[]>(data.TempCurveJson ?? "[]") ?? [];

        PressureCurvePoints = new ObservableCollection<double>(pressureData);
        FlowCurvePoints = new ObservableCollection<double>(flowData);
        TempCurvePoints = new ObservableCollection<double>(tempData);

        PressureMin = (double)data.PressureMin;
        PressureMax = (double)data.PressureMax;
        FlowMin = (double)data.FlowMin;
        FlowMax = (double)data.FlowMax;
        TempMin = (double)data.TempMin;
        TempMax = (double)data.TempMax;
    }

    /// <summary>
    /// 生成模拟曲线数据（批量替换）
    /// </summary>
    private void GenerateSampleCurve()
    {
        var rnd = new Random(SelectedRecord?.RecordCode?.GetHashCode() ?? 42);
        const int n = 200;
        double basePressure = (double)(SelectedRecord?.TestPressure ?? 0.9m);
        double baseFlow = (double)(SelectedRecord?.FinalLeakageRate ?? 0.012m);
        double baseTemp = 24.5;

        var pressureList = new double[n];
        var flowList = new double[n];
        var tempList = new double[n];

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)n;
            double p, f, tp;

            if (t < 0.15)
            {
                double phase = t / 0.15;
                p = basePressure * (1 - Math.Exp(-phase * 4)) + rnd.NextDouble() * 0.02;
                f = baseFlow * (2 + rnd.NextDouble()) * (1 - phase) + rnd.NextDouble() * 0.001;
                tp = baseTemp - 0.3 + rnd.NextDouble() * 0.2;
            }
            else if (t < 0.3)
            {
                double phase = (t - 0.15) / 0.15;
                p = basePressure * (1.05 - 0.05 * phase) + (rnd.NextDouble() - 0.5) * 0.01;
                f = baseFlow * (1.5 + 0.5 * Math.Sin(phase * 10) * (1 - phase)) + (rnd.NextDouble() - 0.5) * 0.003;
                tp = baseTemp + 0.2 * phase + (rnd.NextDouble() - 0.5) * 0.15;
            }
            else
            {
                double phase = (t - 0.3) / 0.7;
                p = basePressure + (rnd.NextDouble() - 0.5) * 0.008 - phase * 0.01;
                f = baseFlow + 0.003 * Math.Sin(phase * 20) + (rnd.NextDouble() - 0.5) * 0.002;
                tp = baseTemp + 0.3 + 0.1 * Math.Sin(phase * 5) + (rnd.NextDouble() - 0.5) * 0.1;
            }

            p = Math.Max(0, p);
            f = Math.Max(0, f);

            pressureList[i] = p;
            flowList[i] = f;
            tempList[i] = tp;
        }

        // 一次替换整个集合
        PressureCurvePoints = new ObservableCollection<double>(pressureList);
        FlowCurvePoints = new ObservableCollection<double>(flowList);
        TempCurvePoints = new ObservableCollection<double>(tempList);

        // 设置合理的范围值
        PressureMin = 0;
        PressureMax = basePressure * 1.2;
        FlowMin = 0;
        FlowMax = baseFlow * 2;
        TempMin = 23.0;
        TempMax = 26.0;
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
