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
public sealed class SystemManagementViewModel : ViewModelBase, IRefreshable
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

    public SystemManagementViewModel()
    {
        UserManagementPage = new UserManagementViewModel();
        RoleManagementPage = new RoleManagementViewModel();
        OperationLogPage = new OperationLogViewModel();

        BackupCommand = new AsyncRelayCommand(ExecuteBackupAsync, () => !_isBackupRunning && PermissionGuard.Can(Perms.BackupView));
        RestoreCommand = new AsyncRelayCommand(ExecuteRestoreAsync, () => !_isRestoreRunning && PermissionGuard.Can(Perms.MigrateView));
        RefreshStatsCommand = new RelayCommand(async () => await RefreshStatisticsAsync());

        _ = InitializeAsync();
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

    public string AutoBackupStatusText => AutoBackupEnabled
        ? $"每 {AutoBackupIntervalHours} 小时自动备份"
        : "已禁用";

    private DispatcherTimer? _autoBackupTimer;

    private void UpdateAutoBackupTimer()
    {
        _autoBackupTimer?.Stop();
        if (!AutoBackupEnabled)
        {
            _autoBackupTimer = null;
            return;
        }

        _autoBackupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(AutoBackupIntervalHours)
        };
        _autoBackupTimer.Tick += async (_, _) => await ExecuteAutoBackupAsync();
        _autoBackupTimer.Start();
    }

    private async Task ExecuteAutoBackupAsync()
    {
        try
        {
            if (IsBackupRunning) return;

            IsBackupRunning = true;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"AutoBackup_{timestamp}.bak";
            var fullPath = Path.Combine(BackupDirectory, fileName);

            if (!Directory.Exists(BackupDirectory))
                Directory.CreateDirectory(BackupDirectory);

            var service = new SystemManagementService();
            await service.BackupDatabaseAsync(fullPath);

            LastBackupTime = DateTime.Now;
            LoadBackupInfo();
            StatusMessage = $"✅ 自动备份完成: {fileName}";

            // 清理过期备份
            CleanupOldBackups();

            // 写入审计日志
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                await logService.LogAsync("数据库备份", "system", $"自动备份到 {fullPath}", "Success");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "写入操作日志失败（自动备份）");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 自动备份失败: {ex.Message}";
        }
        finally
        {
            IsBackupRunning = false;
        }
    }

    private void CleanupOldBackups()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory)) return;
            var cutoff = DateTime.Now.AddDays(-BackupRetentionPolicyDays);
            foreach (var file in Directory.GetFiles(BackupDirectory, "*.bak"))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTime < cutoff)
                    fi.Delete();
            }
            LoadBackupInfo();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理旧备份文件失败");
        }
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

            var service = new SystemManagementService();
            await service.BackupDatabaseAsync(dialog.FileName);

            LastBackupTime = DateTime.Now;
            LoadBackupInfo(); // 刷新备份历史列表
            StatusMessage = $"备份完成: {dialog.FileName}";

            // 写入审计日志
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                var currentUser = UserSession.Current?.User.UserName ?? "system";
                await logService.LogAsync("数据库备份", currentUser, $"备份到 {dialog.FileName}", "Success");
            }
            catch { }
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
