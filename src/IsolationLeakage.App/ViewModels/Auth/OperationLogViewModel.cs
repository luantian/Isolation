using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 操作日志查看视图模型（查询独立的 OperationLogs 表）
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
    private int _retentionDays = OperationLogService.DefaultRetentionDays;
    private int _cleanupPreviewCount;
    private const int PageSize = 50;

    public ObservableCollection<OperationLog> FilteredRecords { get; } = [];

    public List<string> OperationTypes { get; } =
    [
        "全部",
        "登录",
        "登出",
        "创建项目",
        "创建机组",
        "创建用户",
        "修改用户",
        "删除用户",
        "分配角色",
        "创建角色",
        "修改角色",
        "删除角色",
        "分配权限",
        "修改密码",
        "创建试验对象路径",
        "修改试验对象路径",
        "删除试验对象路径",
        "创建测量装置",
        "修改测量装置",
        "删除测量装置",
        "数据上传",
        "批量导入",
        "数据导出",
        "数据库备份",
        "数据库恢复",
        "任务下载",
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
            if (SetProperty(ref _searchText, value)) { }
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

    /// <summary>日志保留天数（默认 90 天）</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    /// <summary>清理预览：将要删除的记录数</summary>
    public int CleanupPreviewCount
    {
        get => _cleanupPreviewCount;
        set => SetProperty(ref _cleanupPreviewCount, value);
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
    public ICommand PreviewCleanupCommand => new AsyncRelayCommand(ExecutePreviewCleanupAsync);
    public ICommand CleanupCommand => new AsyncRelayCommand(ExecuteCleanupAsync);
    public ICommand ExportCommand => new AsyncRelayCommand(ExecuteExportAsync);

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

            var (ids, count) = await SqlHelper.GetPaginatedOperationLogIdsAsync(
                connectionString, CurrentPage, PageSize, OperationTypeFilter, SearchText, DateFrom, DateTo);

            TotalCount = count;

            var logs = new List<OperationLog>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                logs = await context.OperationLogs
                    .Where(l => ids.Contains(l.LogId))
                    .ToListAsync();

                logs = logs.OrderByDescending(l => l.OperationTime).ToList();
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

            var (ids, count) = await SqlHelper.GetPaginatedOperationLogIdsAsync(
                connectionString, CurrentPage, PageSize, OperationTypeFilter, SearchText, DateFrom, DateTo);

            TotalCount = count;

            var logs = new List<OperationLog>();
            if (ids.Count > 0)
            {
                using var context = DbContextFactory.CreateDbContext();
                logs = await context.OperationLogs
                    .Where(l => ids.Contains(l.LogId))
                    .ToListAsync();

                logs = logs.OrderByDescending(l => l.OperationTime).ToList();
            }

            FilteredRecords.Clear();
            foreach (var log in logs)
            {
                FilteredRecords.Add(log);
            }

            StatusMessage = TotalCount == 0
                ? "未找到匹配记录"
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

    /// <summary>预览清理：显示保留天数之前的记录数</summary>
    private async Task ExecutePreviewCleanupAsync()
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-RetentionDays);
            using var context = DbContextFactory.CreateDbContext();
            var service = new OperationLogService(context);

            CleanupPreviewCount = await service.GetCountBeforeAsync(cutoffDate);
            StatusMessage = CleanupPreviewCount == 0
                ? $"无需清理：{cutoffDate:yyyy-MM-dd} 之前没有日志"
                : $"可清理 {CleanupPreviewCount} 条 {cutoffDate:yyyy-MM-dd} 之前的日志";
        }
        catch (Exception ex)
        {
            StatusMessage = $"预览失败：{ex.Message}";
        }
    }

    /// <summary>执行清理：导出并删除保留天数之前的日志</summary>
    private async Task ExecuteCleanupAsync()
    {
        var cutoffDate = DateTime.Now.AddDays(-RetentionDays);

        var result = MessageBox.Show(
            $"将清理 {cutoffDate:yyyy-MM-dd} 之前的所有操作日志。\n" +
            $"清理前会自动导出为 CSV 文件。\n\n" +
            $"保留天数：{RetentionDays} 天\n" +
            $"确认执行？",
            "确认清理日志",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            StatusMessage = "已取消清理";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在导出待清理的日志...";

            using var context = DbContextFactory.CreateDbContext();
            var service = new OperationLogService(context);

            // 先导出
            string exportPath = await service.ExportToCsvAsync(endTime: cutoffDate);
            StatusMessage = $"已导出: {exportPath}\n正在清理...";

            // 再删除
            int deleted = await service.CleanupOldLogsAsync(cutoffDate);
            CleanupPreviewCount = 0;

            StatusMessage = $"✅ 已清理 {deleted} 条日志，导出文件：{exportPath}";

            // 刷新列表
            CurrentPage = 1;
            await LoadFilteredDataAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>导出当前查询范围内的日志</summary>
    private async Task ExecuteExportAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在导出操作日志...";

            using var context = DbContextFactory.CreateDbContext();
            var service = new OperationLogService(context);

            string exportPath = await service.ExportToCsvAsync(
                startTime: DateFrom,
                endTime: DateTo);

            StatusMessage = $"✅ 导出完成：{exportPath}";
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
