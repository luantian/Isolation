using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 操作日志查看视图模型
/// </summary>
public sealed partial class OperationLogViewModel : ObservableObject
{
    private string _operationTypeFilter = string.Empty;
    private string _searchText = string.Empty;
    private DateTime? _dateFrom;
    private DateTime? _dateTo;
    private bool _isLoading;
    private string _statusMessage = "加载中...";
    private int _totalCount;
    private int _currentPage = 1;
    private const int PageSize = 50;

    public ObservableCollection<LoginLog> FilteredRecords { get; } = [];

    public List<string> OperationTypes { get; } =
    [
        "全部",
        "登录",
        "登出",
        "创建用户",
        "修改用户",
        "删除用户",
        "分配角色",
        "创建角色",
        "修改角色",
        "删除角色",
        "分配权限",
        "修改密码",
        "其他"
    ];

    public string OperationTypeFilter
    {
        get => _operationTypeFilter;
        set
        {
            if (SetProperty(ref _operationTypeFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // 可选：输入时自动触发查询，或等待用户点击查询按钮
            }
        }
    }

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

    public int TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public ICommand QueryCommand => new RelayCommand(ApplyQuery);
    public ICommand NextPageCommand => new RelayCommand(GoToNextPage, () => CanGoToNextPage());
    public ICommand PreviousPageCommand => new RelayCommand(GoToPreviousPage, () => CanGoToPreviousPage());

    public OperationLogViewModel()
    {
        _ = LoadDataAsync();
    }

    /// <summary>
    /// 异步加载初始数据
    /// </summary>
    private async Task LoadDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载操作日志...";

            var connectionString = DbContextFactory.GetDefaultConnectionString();

            // 使用原始 SQL ROW_NUMBER() 分页（兼容 SQL Server 2008 R2）
            var (ids, count) = await SqlHelper.GetPaginatedLoginLogIdsAsync(
                connectionString, CurrentPage, PageSize, OperationTypeFilter, SearchText, DateFrom, DateTo);

            TotalCount = count;

            var logs = new List<LoginLog>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                logs = await context.LoginLogs
                    .Where(l => ids.Contains(l.LogId))
                    .ToListAsync();

                logs = logs.OrderByDescending(l => l.LoginTime).ToList();
            }

            FilteredRecords.Clear();
            foreach (var log in logs)
            {
                FilteredRecords.Add(log);
            }

            StatusMessage = TotalCount == 0
                ? "暂无操作日志"
                : $"共 {TotalCount} 条记录，第 {CurrentPage} 页";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            UpdatePageCommands();
        }
    }

    /// <summary>
    /// 应用查询过滤
    /// </summary>
    public async void ApplyQuery()
    {
        CurrentPage = 1;
        await LoadFilteredDataAsync();
    }

    /// <summary>
    /// 加载过滤后的数据
    /// </summary>
    private async Task LoadFilteredDataAsync()
    {
        try
        {
            IsLoading = true;

            var connectionString = DbContextFactory.GetDefaultConnectionString();

            // 使用原始 SQL ROW_NUMBER() 分页（兼容 SQL Server 2008 R2）
            var (ids, count) = await SqlHelper.GetPaginatedLoginLogIdsAsync(
                connectionString, CurrentPage, PageSize, OperationTypeFilter, SearchText, DateFrom, DateTo);

            TotalCount = count;

            var logs = new List<LoginLog>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                logs = await context.LoginLogs
                    .Where(l => ids.Contains(l.LogId))
                    .ToListAsync();

                logs = logs.OrderByDescending(l => l.LoginTime).ToList();
            }

            FilteredRecords.Clear();
            foreach (var log in logs)
            {
                FilteredRecords.Add(log);
            }

            StatusMessage = TotalCount == 0
                ? "未找到匹配的记录"
                : $"共 {TotalCount} 条记录，第 {CurrentPage} 页";
        }
        catch (Exception ex)
        {
            StatusMessage = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            UpdatePageCommands();
        }
    }

    /// <summary>
    /// 构建查询条件
    /// </summary>
    private IQueryable<LoginLog> BuildQuery(AppDbContext context)
    {
        var query = context.LoginLogs.AsQueryable();

        // 操作类型过滤
        if (!string.IsNullOrEmpty(OperationTypeFilter) && OperationTypeFilter != "全部")
        {
            query = query.Where(l => l.UserAgent != null && l.UserAgent.Contains($"Operation: {OperationTypeFilter}"));
        }

        // 用户名搜索
        var keyword = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(l =>
                EF.Functions.Like(l.UserName, $"%{keyword}%") ||
                (l.ClientIp != null && EF.Functions.Like(l.ClientIp, $"%{keyword}%")));
        }

        // 日期范围过滤
        if (DateFrom.HasValue)
        {
            query = query.Where(l => l.LoginTime >= DateFrom.Value);
        }

        if (DateTo.HasValue)
        {
            // DateTo 包含当天全天
            var endOfDay = DateTo.Value.Date.AddDays(1).AddTicks(-1);
            query = query.Where(l => l.LoginTime <= endOfDay);
        }

        return query;
    }

    private void GoToNextPage()
    {
        CurrentPage++;
        _ = LoadFilteredDataAsync();
    }

    private void GoToPreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            _ = LoadFilteredDataAsync();
        }
    }

    private bool CanGoToNextPage()
    {
        return CurrentPage < (int)Math.Ceiling(TotalCount / (double)PageSize);
    }

    private bool CanGoToPreviousPage()
    {
        return CurrentPage > 1;
    }

    private void UpdatePageCommands()
    {
        ((RelayCommand)NextPageCommand).NotifyCanExecuteChanged();
        ((RelayCommand)PreviousPageCommand).NotifyCanExecuteChanged();
    }
}
