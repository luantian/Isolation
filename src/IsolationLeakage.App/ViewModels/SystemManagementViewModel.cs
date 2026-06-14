using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.ViewModels.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 系统管理视图模型 - 基于标签页的容器
/// </summary>
public sealed class SystemManagementViewModel : ViewModelBase
{
    private string _activeTab = "UserManagement";
    private bool _isBackupRunning;
    private bool _isRestoreRunning;
    private bool _isExportRunning;
    private bool _isImportRunning;
    private string _statusMessage = "就绪";
    private long _totalUsers;
    private long _totalRoles;
    private long _totalMenus;
    private long _totalLogEntries;
    private long _databaseSizeBytes;
    private DateTime? _lastBackupTime;

    public SystemManagementViewModel()
    {
        UserManagementPage = new UserManagementViewModel();
        RoleManagementPage = new RoleManagementViewModel();
        MenuManagementPage = new MenuManagementViewModel();
        OperationLogPage = new OperationLogViewModel();

        BackupCommand = new AsyncRelayCommand(ExecuteBackupAsync, () => !_isBackupRunning);
        RestoreCommand = new AsyncRelayCommand(ExecuteRestoreAsync, () => !_isRestoreRunning);
        ExportCommand = new AsyncRelayCommand(ExecuteExportAsync, () => !_isExportRunning);
        ImportCommand = new AsyncRelayCommand(ExecuteImportAsync, () => !_isImportRunning);
        RefreshStatsCommand = new RelayCommand(async () => await RefreshStatisticsAsync());

        _ = InitializeAsync();
    }

    #region Sub-ViewModels

    /// <summary>用户管理子视图模型</summary>
    public UserManagementViewModel UserManagementPage { get; }

    /// <summary>角色管理子视图模型</summary>
    public RoleManagementViewModel RoleManagementPage { get; }

    /// <summary>菜单管理子视图模型</summary>
    public MenuManagementViewModel MenuManagementPage { get; }

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
                OnPropertyChanged(nameof(IsMenuManagementActive));
                OnPropertyChanged(nameof(IsOperationLogActive));
                OnPropertyChanged(nameof(IsBackupActive));
                OnPropertyChanged(nameof(IsMigrationActive));
            }
        }
    }

    public bool IsUserManagementActive => _activeTab == "UserManagement";
    public bool IsRoleManagementActive => _activeTab == "RoleManagement";
    public bool IsMenuManagementActive => _activeTab == "MenuManagement";
    public bool IsOperationLogActive => _activeTab == "OperationLog";
    public bool IsBackupActive => _activeTab == "Backup";
    public bool IsMigrationActive => _activeTab == "Migration";

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

    public bool IsExportRunning
    {
        get => _isExportRunning;
        set
        {
            if (SetProperty(ref _isExportRunning, value))
            {
                ((AsyncRelayCommand)ExportCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsImportRunning
    {
        get => _isImportRunning;
        set
        {
            if (SetProperty(ref _isImportRunning, value))
            {
                ((AsyncRelayCommand)ImportCommand).NotifyCanExecuteChanged();
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

    public long TotalMenus
    {
        get => _totalMenus;
        set => SetProperty(ref _totalMenus, value);
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

    public string LastBackupDisplay => _lastBackupTime.HasValue
        ? _lastBackupTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
        : "从未备份";

    #endregion

    #region Commands

    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand RefreshStatsCommand { get; }

    #endregion

    #region Private Methods

    private async Task InitializeAsync()
    {
        await RefreshStatisticsAsync();
        LoadBackupInfo();
    }

    private async Task RefreshStatisticsAsync()
    {
        try
        {
            using var context = IsolationLeakage.App.Data.DbContextFactory.CreateDbContext();

            TotalUsers = await context.Users.LongCountAsync();
            TotalRoles = await context.Roles.LongCountAsync();
            TotalMenus = await context.Menus.LongCountAsync();
            TotalLogEntries = await context.LoginLogs.LongCountAsync();
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
        }
        catch
        {
            // 静默失败，不影响主流程
        }
    }

    private async Task ExecuteBackupAsync()
    {
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
            StatusMessage = $"备份完成: {dialog.FileName}";
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

    private async Task ExecuteExportAsync()
    {
        try
        {
            IsExportRunning = true;
            StatusMessage = "正在导出数据...";

            var dialog = new SaveFileDialog
            {
                Filter = "SQL Script Files (*.sql)|*.sql|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = $"IsolationLeakage_Data_{DateTime.Now:yyyyMMdd_HHmmss}.sql",
                Title = "选择数据导出文件保存位置"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "已取消导出";
                return;
            }

            var service = new SystemManagementService();
            await service.ExportDataAsync(dialog.FileName);

            StatusMessage = $"导出完成: {dialog.FileName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "导出已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
        finally
        {
            IsExportRunning = false;
        }
    }

    private async Task ExecuteImportAsync()
    {
        try
        {
            IsImportRunning = true;
            StatusMessage = "请选择 SQL 脚本文件进行导入...";

            var dialog = new OpenFileDialog
            {
                Filter = "SQL Script Files (*.sql)|*.sql|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                Title = "选择 SQL 脚本文件进行导入"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "已取消导入";
                return;
            }

            if (!File.Exists(dialog.FileName))
            {
                StatusMessage = "脚本文件不存在";
                return;
            }

            var service = new SystemManagementService();
            await service.ImportDataAsync(dialog.FileName);

            StatusMessage = $"导入完成: {dialog.FileName}";
            await RefreshStatisticsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "导入已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败: {ex.Message}";
        }
        finally
        {
            IsImportRunning = false;
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
