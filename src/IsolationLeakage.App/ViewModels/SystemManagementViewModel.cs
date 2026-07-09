using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using IsolationLeakage.App.ViewModels.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 系统管理视图模型 - 基于标签页的容器
/// </summary>
public sealed class SystemManagementViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private string _activeTab = "UserManagement";
    private bool _isBackupRunning;
    private bool _isRestoreRunning;
    private string _statusMessage = "就绪";
    private long _totalUsers;
    private long _totalRoles;
    private long _totalLogEntries;
    private long _databaseSizeBytes;
    private DateTime? _lastBackupTime;
    private ObservableCollection<BackupFileInfo> _backupHistoryList = [];
    private bool _disposed;

    public SystemManagementViewModel()
    {
        UserManagementPage = new UserManagementViewModel();
        RoleManagementPage = new RoleManagementViewModel();
        OperationLogPage = new OperationLogViewModel();

        BackupCommand = new AsyncRelayCommand(ExecuteBackupAsync, () => !_isBackupRunning && PermissionGuard.Can(Perms.BackupView));
        RestoreCommand = new AsyncRelayCommand(ExecuteRestoreAsync, () => !_isRestoreRunning && PermissionGuard.Can(Perms.MigrateView));
        RefreshStatsCommand = new RelayCommand(async () => await RefreshStatisticsAsync());

        // 监听自动备份服务状态变化，实时更新 UI
        AutoBackupService.Instance.BackupStatusChanged += OnBackupStatusChanged;
        AutoBackupService.Instance.BackupCompleted += OnBackupCompleted;

        _ = InitializeAsync();
    }

    /// <summary>
    /// 释放资源，取消事件订阅防止内存泄漏
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        AutoBackupService.Instance.BackupStatusChanged -= OnBackupStatusChanged;
        AutoBackupService.Instance.BackupCompleted -= OnBackupCompleted;
        _disposed = true;
    }

    /// <summary>
    /// 备份服务状态变更处理
    /// </summary>
    private void OnBackupStatusChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AutoBackupStatusText));
        LastBackupTime = AutoBackupService.Instance.LastBackupTime;
        LoadBackupInfo();
    }

    /// <summary>
    /// 备份完成事件处理（成功/失败都会触发）
    /// </summary>
    private void OnBackupCompleted(object? sender, BackupCompletedEventArgs e)
    {
        // 更新备份状态
        OnPropertyChanged(nameof(AutoBackupStatusText));
        LastBackupTime = AutoBackupService.Instance.LastBackupTime;
        LoadBackupInfo();

        // 备份失败时，在 UI 上显示明显提示
        if (!e.Success)
        {
            StatusMessage = $"❌ 备份失败: {e.ErrorMessage}";

            // 安全地弹出提示（检查应用是否还在运行，避免退出时报错）
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.InvokeAsync(() =>
                {
                    // 再检查一次，防止在调度期间应用已退出
                    if (Application.Current != null && !dispatcher.HasShutdownStarted)
                    {
                        MessageBox.Show(
                            $"自动备份失败：\n{e.ErrorMessage}\n\n请检查数据库连接和备份目录权限。",
                            "备份失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                });
            }
        }
        else
        {
            // 备份成功，静默更新状态（不弹窗打扰用户）
            var fileName = string.IsNullOrEmpty(e.BackupFilePath)
                ? "已完成"
                : Path.GetFileName(e.BackupFilePath);
            StatusMessage = $"✅ 备份成功: {fileName}";
        }
    }

    #region Sub-ViewModels

    /// <summary>用户管理子视图模型</summary>
    public UserManagementViewModel UserManagementPage { get; }

    /// <summary>角色管理子视图模型</summary>
    public RoleManagementViewModel RoleManagementPage { get; }

    /// <summary>操作日志子视图模型</summary>
    public OperationLogViewModel OperationLogPage { get; }

    #endregion

    #region Active Tab

    /// <summary>当前激活的标签页标识</summary>
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsUserManagementActive));
                OnPropertyChanged(nameof(IsRoleManagementActive));
                OnPropertyChanged(nameof(IsOperationLogActive));
                OnPropertyChanged(nameof(IsBackupActive));
            }
        }
    }

    public bool IsUserManagementActive => _activeTab == "UserManagement";
    public bool IsRoleManagementActive => _activeTab == "RoleManagement";
    public bool IsOperationLogActive => _activeTab == "OperationLog";
    public bool IsBackupActive => _activeTab == "Backup";

    #endregion

    #region Loading States

    public bool IsBackupRunning
    {
        get => _isBackupRunning;
        set
        {
            if (SetProperty(ref _isBackupRunning, value))
            {
                ((AsyncRelayCommand)BackupCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRestoreRunning
    {
        get => _isRestoreRunning;
        set
        {
            if (SetProperty(ref _isRestoreRunning, value))
            {
                ((AsyncRelayCommand)RestoreCommand).NotifyCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Status & Statistics

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public long TotalUsers
    {
        get => _totalUsers;
        set => SetProperty(ref _totalUsers, value);
    }

    public long TotalRoles
    {
        get => _totalRoles;
        set => SetProperty(ref _totalRoles, value);
    }

    public long TotalLogEntries
    {
        get => _totalLogEntries;
        set => SetProperty(ref _totalLogEntries, value);
    }

    public long DatabaseSizeBytes
    {
        get => _databaseSizeBytes;
        set => SetProperty(ref _databaseSizeBytes, value);
    }

    public string DatabaseSizeDisplay => FormatFileSize(_databaseSizeBytes);

    public DateTime? LastBackupTime
    {
        get => _lastBackupTime;
        set
        {
            if (SetProperty(ref _lastBackupTime, value))
            {
                OnPropertyChanged(nameof(LastBackupDisplay));
            }
        }
    }

    public ObservableCollection<BackupFileInfo> BackupHistoryList
    {
        get => _backupHistoryList;
        set => SetProperty(ref _backupHistoryList, value);
    }

    // ================ 定期备份配置 ================
    // 加载配置期间为 true，避免 setter 触发回写/定时器重建
    private bool _isLoadingBackupConfig;

    private bool _autoBackupEnabled;
    public bool AutoBackupEnabled
    {
        get => _autoBackupEnabled;
        set
        {
            if (SetProperty(ref _autoBackupEnabled, value))
            {
                UpdateAutoBackupTimer();
                OnPropertyChanged(nameof(AutoBackupStatusText));
                SaveBackupConfig();
            }
        }
    }

    private int _autoBackupIntervalHours = 24;
    public int AutoBackupIntervalHours
    {
        get => _autoBackupIntervalHours;
        set
        {
            if (SetProperty(ref _autoBackupIntervalHours, value))
            {
                UpdateAutoBackupTimer();
                OnPropertyChanged(nameof(AutoBackupStatusText));
                SaveBackupConfig();
            }
        }
    }

    private int _backupRetentionPolicyDays = 30;
    public int BackupRetentionPolicyDays
    {
        get => _backupRetentionPolicyDays;
        set
        {
            if (SetProperty(ref _backupRetentionPolicyDays, value))
            {
                SaveBackupConfig();
            }
        }
    }

    private string _backupDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
    public string BackupDirectory
    {
        get => _backupDirectory;
        set
        {
            if (SetProperty(ref _backupDirectory, value))
            {
                SaveBackupConfig();
            }
        }
    }

    /// <summary>
    /// 从 user-settings.json 加载用户配置
    /// </summary>
    private void LoadBackupConfig()
    {
        _isLoadingBackupConfig = true;
        try
        {
            var cfg = AppConfiguration.GetUserSettings().Backup;
            _autoBackupIntervalHours = cfg.AutoBackupIntervalHours > 0 ? cfg.AutoBackupIntervalHours : 24;
            _backupRetentionPolicyDays = cfg.BackupRetentionPolicyDays > 0 ? cfg.BackupRetentionPolicyDays : 30;
            _backupDirectory = string.IsNullOrWhiteSpace(cfg.BackupDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups")
                : cfg.BackupDirectory;
            // AutoBackupEnabled 最后设置，触发定时器按已加载的间隔启动
            _autoBackupEnabled = cfg.AutoBackupEnabled;

            OnPropertyChanged(nameof(AutoBackupIntervalHours));
            OnPropertyChanged(nameof(BackupRetentionPolicyDays));
            OnPropertyChanged(nameof(BackupDirectory));
            OnPropertyChanged(nameof(AutoBackupEnabled));
            OnPropertyChanged(nameof(AutoBackupStatusText));

            UpdateAutoBackupTimer();
        }
        catch
        {
            // 静默失败，使用默认值
        }
        finally
        {
            _isLoadingBackupConfig = false;
        }
    }

    /// <summary>
    /// 将当前备份配置写入 user-settings.json
    /// </summary>
    private void SaveBackupConfig()
    {
        if (_isLoadingBackupConfig) return;
        try
        {
            // 读取现有配置，只更新备份部分（保留其他用户设置）
            var settings = AppConfiguration.GetUserSettings();
            settings.Backup.AutoBackupEnabled = _autoBackupEnabled;
            settings.Backup.AutoBackupIntervalHours = _autoBackupIntervalHours;
            settings.Backup.BackupRetentionPolicyDays = _backupRetentionPolicyDays;
            settings.Backup.BackupDirectory = _backupDirectory;
            AppConfiguration.SaveUserSettings(settings);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存配置失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 自动备份状态显示
    /// </summary>
    public string AutoBackupStatusText
    {
        get
        {
            if (!AutoBackupEnabled)
                return "已禁用";

            // 显示上次备份状态（如果有错误）
            var lastSuccess = AutoBackupService.Instance.LastBackupSucceeded;
            if (lastSuccess == false)
            {
                var error = AutoBackupService.Instance.LastBackupError ?? "未知错误";
                var shortError = error.Length > 30 ? error.Substring(0, 30) + "..." : error;
                return $"⚠️ 上次备份失败: {shortError}";
            }

            var nextTime = AutoBackupService.Instance.NextBackupTime;
            return nextTime.HasValue
                ? $"每 {AutoBackupIntervalHours} 小时自动备份（下次: {nextTime.Value:yyyy-MM-dd HH:mm}）"
                : $"每 {AutoBackupIntervalHours} 小时自动备份";
        }
    }

    /// <summary>
    /// 更新自动备份定时器（委托给后台服务）
    /// </summary>
    private void UpdateAutoBackupTimer()
    {
        AutoBackupService.Instance.UpdateTimer();
        OnPropertyChanged(nameof(AutoBackupStatusText));
    }

    public string LastBackupDisplay => _lastBackupTime.HasValue
        ? _lastBackupTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
        : "从未备份";

    #endregion

    #region Commands

    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RefreshStatsCommand { get; }

    #endregion

    #region Private Methods

    private async Task InitializeAsync()
    {
        LoadBackupConfig();
        await RefreshStatisticsAsync();
        LoadBackupInfo();
    }

    Task IRefreshable.RefreshAsync() => RefreshStatisticsAsync();

    private async Task RefreshStatisticsAsync()
    {
        try
        {
            using var context = IsolationLeakage.App.Data.DbContextFactory.CreateDbContext();

            TotalUsers = await context.Users.LongCountAsync();
            TotalRoles = await context.Roles.LongCountAsync();
            TotalLogEntries = await context.OperationLogs.LongCountAsync();
            DatabaseSizeBytes = await GetDatabaseSizeAsync(context);

            OnPropertyChanged(nameof(DatabaseSizeDisplay));
            StatusMessage = "统计信息已刷新";
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新统计失败: {ex.Message}";
        }
    }

    private void LoadBackupInfo()
    {
        try
        {
            var service = new SystemManagementService();
            var backups = service.GetBackupList();
            LastBackupTime = backups.FirstOrDefault()?.CreatedTime;
            BackupHistoryList = new ObservableCollection<BackupFileInfo>(backups);
        }
        catch
        {
            // 静默失败，不影响主流程
        }
    }

    private async Task ExecuteBackupAsync()
    {
        PermissionGuard.Require(Perms.BackupView);
        try
        {
            IsBackupRunning = true;
            StatusMessage = "正在执行数据库备份...";

            var dialog = new SaveFileDialog
            {
                Filter = "SQL Server Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
                FileName = $"IsolationLeakage_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.bak",
                Title = "选择备份文件保存位置"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "已取消备份";
                return;
            }

            var currentUser = UserSession.Current?.User.UserName ?? "system";

            // 使用统一的备份入口（AutoBackupService 确保线程安全并自动记录审计日志）
            var success = await AutoBackupService.Instance.ExecuteBackupAsync(currentUser, dialog.FileName);

            LoadBackupInfo(); // 刷新备份历史列表
            StatusMessage = success ? $"备份完成: {dialog.FileName}" : "备份失败，请查看日志";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已有备份任务正在执行"))
        {
            StatusMessage = "已有备份任务正在执行，请稍后再试";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "备份已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"备份失败: {ex.Message}";
        }
        finally
        {
            IsBackupRunning = false;
        }
    }

    private async Task ExecuteRestoreAsync()
    {
        try
        {
            PermissionGuard.Require(Perms.MigrateView);
            // 确认对话框 — 还原操作会覆盖整个数据库
            var confirmResult = MessageBox.Show(
                "⚠ 数据库还原将覆盖当前所有数据！\n\n此操作不可撤销，建议先执行备份。\n\n确定要继续吗？",
                "确认数据库还原",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmResult != MessageBoxResult.OK) return;

            IsRestoreRunning = true;
            StatusMessage = "请选择备份文件进行还原...";

            var dialog = new OpenFileDialog
            {
                Filter = "SQL Server Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
                Title = "选择备份文件进行还原"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "已取消还原";
                return;
            }

            if (!File.Exists(dialog.FileName))
            {
                StatusMessage = "备份文件不存在";
                return;
            }

            var service = new SystemManagementService();
            await service.RestoreDatabaseAsync(dialog.FileName);

            LastBackupTime = DateTime.Now;
            StatusMessage = $"还原完成: {dialog.FileName}";

            // 写入审计日志
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                var currentUser = UserSession.Current?.User.UserName ?? "system";
                await logService.LogAsync("数据库还原", currentUser, $"从 {dialog.FileName} 还原", "Success");
            }
            catch { }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "还原已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"还原失败: {ex.Message}";
        }
        finally
        {
            IsRestoreRunning = false;
        }
    }

    private static async Task<long> GetDatabaseSizeAsync(IsolationLeakage.App.Data.AppDbContext context)
    {
        try
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT SUM(size) * 8192 AS SizeInBytes
                FROM sys.database_files
                WHERE type_desc = 'ROWS'";

            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt64(result) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    #endregion
}
