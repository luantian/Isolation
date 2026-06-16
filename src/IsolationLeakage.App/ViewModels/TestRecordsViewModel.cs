using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验记录视图模型（简化版 - 只负责记录查询和详情展示）
/// </summary>
public sealed partial class TestRecordsViewModel : ViewModelBase
{
    private TestRecord? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "全部";
    private bool _isLoading;
    private string _statusMessage = "加载中...";
    private int _totalCount;
    private int _currentPage = 1;
    private int _pageSize = 10;

    // 曲线数据
    public ObservableCollection<double> PressureCurvePoints { get; } = [];
    public ObservableCollection<double> FlowCurvePoints { get; } = [];
    public ObservableCollection<double> TempCurvePoints { get; } = [];

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
        FilteredRecords = [];
        _ = LoadDataAsync();
    }

    public ObservableCollection<TestRecord> FilteredRecords { get; }

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
    private async void ApplyPageNavigation(int page)
    {
        if (page < 1 || page > TotalPages) return;

        CurrentPage = page;
        await ApplyQueryWithPagination();
    }

    public TestRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                UpdateChartFromSelected();
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

    /// <summary>
    /// 删除选中记录命令
    /// </summary>
    public ICommand DeleteSelectedCommand => new RelayCommand(
        async () => await DeleteSelectedAsync(),
        () => Services.Security.UserSession.HasPermission("records:data:upload"));

    private async Task DeleteSelectedAsync()
    {
        if (SelectedRecord == null)
            return;

        // 1. 确认框
        var result = System.Windows.MessageBox.Show(
            $"确定要删除试验记录 [{SelectedRecord.RecordCode}] 吗？\n\n此操作不可恢复！",
            "确认删除",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.OK)
            return;

        try
        {
            IsLoading = true;
            StatusMessage = "正在删除...";

            using var context = DbContextFactory.CreateDbContext();

            // 2. 删除过程数据（如果有）
            var processData = await context.TestProcessData
                .FirstOrDefaultAsync(p => p.RecordCode == SelectedRecord.RecordCode);

            if (processData != null)
                context.TestProcessData.Remove(processData);

            // 3. 删除主记录
            var recordToDelete = await context.TestRecords
                .FirstOrDefaultAsync(r => r.RecordCode == SelectedRecord.RecordCode);

            if (recordToDelete != null)
                context.TestRecords.Remove(recordToDelete);

            // 4. 记录操作日志（在 SaveChanges 之前，context 还在）
            try
            {
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
                await logService.LogAsync(
                    "删除试验记录",
                    currentUser,
                    $"删除试验记录 [{SelectedRecord.RecordCode}] - 对象: {SelectedRecord.ObjectCode}, 试验时间: {SelectedRecord.TestTime:yyyy-MM-dd HH:mm}",
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
            System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 初始加载数据
    /// </summary>
    private async Task LoadDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载...";

            var connectionString = DbContextFactory.GetDefaultConnectionString();
            WriteLog($"LoadDataAsync start, Page={CurrentPage}, PageSize={PageSize}, Filter={ResultFilter}");

            // 使用原始 SQL ROW_NUMBER() 分页（兼容 SQL Server 2008 R2）
            var (ids, count) = await SqlHelper.GetPaginatedRecordIdsAsync(
                connectionString, CurrentPage, PageSize, ResultFilter, SearchText);

            WriteLog($"SqlHelper returned: Count={count}, IDs={ids.Count}");
            TotalCount = count;

            // 通过 ID 列表加载完整记录（含导航属性）
            var records = new List<TestRecord>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                records = await context.TestRecords
                    .Include(r => r.Project)
                    .Include(r => r.Unit)
                    .Include(r => r.Device)
                    .Where(r => ids.Contains(r.RecordCode))
                    .ToListAsync();

                WriteLog($"EF loaded {records.Count} records with navigation properties");
                records = records.OrderByDescending(r => r.TestTime).ToList();
            }

            FilteredRecords.Clear();
            var startIndex = (CurrentPage - 1) * PageSize;
            for (int i = 0; i < records.Count; i++)
            {
                records[i].RowNumber = startIndex + i + 1;
                FilteredRecords.Add(records[i]);
            }

            SelectedRecord = FilteredRecords.FirstOrDefault();
            StatusMessage = PaginationStatus;
            WriteLog($"LoadDataAsync completed: TotalCount={TotalCount}, Displayed={records.Count}");
        }
        catch (Exception ex)
        {
            var msg = $"加载失败：{ex.Message}";
            StatusMessage = msg;
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            WriteLog($"ERROR: {msg}\n{ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 应用查询过滤（带分页）
    /// </summary>
    private async Task ApplyQueryWithPagination()
    {
        try
        {
            IsLoading = true;

            var connectionString = DbContextFactory.GetDefaultConnectionString();

            // 使用原始 SQL ROW_NUMBER() 分页（兼容 SQL Server 2008 R2）
            var (ids, count) = await SqlHelper.GetPaginatedRecordIdsAsync(
                connectionString, CurrentPage, PageSize, ResultFilter, SearchText);

            TotalCount = count;

            // 页码边界检查
            if (CurrentPage > TotalPages)
                CurrentPage = TotalPages > 0 ? TotalPages : 1;

            // 通过 ID 列表加载完整记录（含导航属性）
            var records = new List<TestRecord>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                records = await context.TestRecords
                    .Include(r => r.Project)
                    .Include(r => r.Unit)
                    .Include(r => r.Device)
                    .Where(r => ids.Contains(r.RecordCode))
                    .ToListAsync();

                // 按 TestTime 倒序排列（与分页查询一致）
                records = records.OrderByDescending(r => r.TestTime).ToList();
            }

            FilteredRecords.Clear();
            var startIndex = (CurrentPage - 1) * PageSize;
            for (int i = 0; i < records.Count; i++)
            {
                records[i].RowNumber = startIndex + i + 1;
                FilteredRecords.Add(records[i]);
            }

            SelectedRecord = FilteredRecords.FirstOrDefault();
            StatusMessage = PaginationStatus;
        }
        catch (Exception ex)
        {
            StatusMessage = $"查询失败：{ex.Message}";
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 应用查询过滤
    /// </summary>
    public async void ApplyQuery()
    {
        // 查询时回到第一页
        CurrentPage = 1;
        await ApplyQueryWithPagination();
    }

    /// <summary>
    /// 更新选中记录的曲线数据
    /// </summary>
    private async void UpdateChartFromSelected()
    {
        PressureCurvePoints.Clear();
        FlowCurvePoints.Clear();
        TempCurvePoints.Clear();

        if (SelectedRecord == null)
        {
            // 设置默认范围
            PressureMin = 0;
            PressureMax = 1;
            FlowMin = 0;
            FlowMax = 0.01;
            TempMin = 20;
            TempMax = 30;
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var processData = await context.TestProcessData
                .FirstOrDefaultAsync(d => d.RecordCode == SelectedRecord.RecordCode);

            if (processData != null)
            {
                var pressureData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.PressureCurveJson ?? "[]") ?? [];
                var flowData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.FlowCurveJson ?? "[]") ?? [];
                var tempData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.TempCurveJson ?? "[]") ?? [];

                foreach (var p in pressureData) PressureCurvePoints.Add(p);
                foreach (var f in flowData) FlowCurvePoints.Add(f);
                foreach (var t in tempData) TempCurvePoints.Add(t);

                // 使用数据库中存储的范围
                PressureMin = (double)processData.PressureMin;
                PressureMax = (double)processData.PressureMax;
                FlowMin = (double)processData.FlowMin;
                FlowMax = (double)processData.FlowMax;
                TempMin = (double)processData.TempMin;
                TempMax = (double)processData.TempMax;

                return;
            }
        }
        catch
        {
            // 读取失败时生成模拟数据
        }

        // 生成模拟曲线数据（没有真实数据时，用于展示）
        GenerateSampleCurve();
    }

    /// <summary>
    /// 生成模拟曲线数据（用于展示效果）
    /// </summary>
    private void GenerateSampleCurve()
    {
        var rnd = new Random(SelectedRecord?.RecordCode?.GetHashCode() ?? 42);
        const int n = 200;
        double basePressure = (double)(SelectedRecord?.TestPressure ?? 0.9m);
        double baseFlow = (double)(SelectedRecord?.FinalLeakageRate ?? 0.012m);
        double baseTemp = 24.5;

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

            PressureCurvePoints.Add(p);
            FlowCurvePoints.Add(f);
            TempCurvePoints.Add(tp);
        }

        // 设置合理的范围值
        PressureMin = 0;
        PressureMax = basePressure * 1.2;
        FlowMin = 0;
        FlowMax = baseFlow * 2;
        TempMin = 23.0;
        TempMax = 26.0;
    }

    /// <summary>
    /// 构建查询（包含关联数据和过滤条件）
    /// </summary>
    private IQueryable<TestRecord> BuildBaseQuery(AppDbContext context)
    {
        var query = context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.Device)
            .AsQueryable();

        // 结果过滤
        if (ResultFilter != "全部")
        {
            var targetResult = ResultFilter == "合格" ? TestResult.Pass : TestResult.Fail;
            query = query.Where(r => r.Result == targetResult);
        }

        // 关键字搜索
        var keyword = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(r =>
                EF.Functions.Like(r.RecordCode, $"%{keyword}%") ||
                EF.Functions.Like(r.ObjectCode, $"%{keyword}%") ||
                EF.Functions.Like(r.ObjectName, $"%{keyword}%") ||
                EF.Functions.Like(r.DeviceCode, $"%{keyword}%") ||
                EF.Functions.Like(r.DataPackageName, $"%{keyword}%"));
        }

        return query;
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
        catch { }
    }
}
