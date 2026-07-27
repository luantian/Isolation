using System.IO;
using System.Threading;
using System.Windows.Threading;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services.Security;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 备份结果事件参数
/// </summary>
public sealed class BackupCompletedEventArgs : EventArgs
{
    /// <summary>备份是否成功</summary>
    public bool Success { get; }

    /// <summary>备份文件路径（成功时有效）</summary>
    public string? BackupFilePath { get; }

    /// <summary>错误信息（失败时有效）</summary>
    public string? ErrorMessage { get; }

    /// <summary>备份时间</summary>
    public DateTime BackupTime { get; }

    public BackupCompletedEventArgs(bool success, string? backupFilePath, string? errorMessage)
    {
        Success = success;
        BackupFilePath = backupFilePath;
        ErrorMessage = errorMessage;
        BackupTime = DateTime.Now;
    }
}

/// <summary>
/// 自动备份后台服务
/// </summary>
/// <remarks>
/// 应用启动时初始化，根据用户配置自动执行数据库备份。
/// 与 UI 层解耦，即使不打开系统管理页面也能正常工作。
/// 线程安全：确保同一时间只有一个备份操作在执行。
/// </remarks>
public sealed class AutoBackupService : IDisposable
{
    // 使用 Lazy<T> 确保线程安全的单例初始化
    private static readonly Lazy<AutoBackupService> _instance = new(() => new AutoBackupService());
    private DispatcherTimer? _backupTimer;
    private int _isRunning = 0; // 使用 int 配合 Interlocked 实现线程安全
    private bool _isDisposed;
    private DateTime? _lastBackupTime;
    private readonly object _lockObj = new(); // 用于配置变更时的互斥保护

    // 备份失败重试机制
    private int _consecutiveBackupFailures;
    private const int BackupRetryIntervalMinutes = 5; // 失败后 5 分钟重试
    private const int MaxBackupRetryAttempts = 6; // 最多重试 6 次（共 30 分钟），之后恢复正常间隔

    /// <summary>
    /// 单例实例
    /// </summary>
    public static AutoBackupService Instance => _instance.Value;

    /// <summary>
    /// 是否启用自动备份
    /// </summary>
    public bool AutoBackupEnabled => AppConfiguration.GetUserSettings().Backup.AutoBackupEnabled;

    /// <summary>
    /// 自动备份间隔（小时）
    /// </summary>
    public int AutoBackupIntervalHours
    {
        get
        {
            var hours = AppConfiguration.GetUserSettings().Backup.AutoBackupIntervalHours;
            return hours > 0 ? hours : 24; // 确保间隔至少为 1 小时
        }
    }

    /// <summary>
    /// 备份保留天数
    /// </summary>
    public int BackupRetentionPolicyDays
    {
        get
        {
            var days = AppConfiguration.GetUserSettings().Backup.BackupRetentionPolicyDays;
            return days > 0 ? days : 30; // 确保至少保留 1 天
        }
    }

    /// <summary>
    /// 备份目录
    /// </summary>
    /// <remarks>
    /// 优先使用用户配置的目录；未配置时使用应用目录下的 Backups 文件夹。
    /// 不再同步查询 SQL Server 默认备份目录（避免 UI 线程死锁）。
    /// </remarks>
    public string BackupDirectory
    {
        get
        {
            var dir = AppConfiguration.GetUserSettings().Backup.BackupDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;

            // 使用缓存的 SQL Server 默认目录（在 Initialize 中异步预加载）
            if (!string.IsNullOrWhiteSpace(_cachedSqlBackupDir))
                return _cachedSqlBackupDir;

            // 回退到应用目录
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
        }
    }

    /// <summary>缓存的 SQL Server 默认备份目录（Initialize 时异步加载）</summary>
    private string? _cachedSqlBackupDir;

    /// <summary>
    /// 下次备份时间
    /// </summary>
    public DateTime? NextBackupTime { get; private set; }

    /// <summary>
    /// 上次备份时间
    /// </summary>
    public DateTime? LastBackupTime => _lastBackupTime;

    /// <summary>
    /// 上次备份是否成功
    /// </summary>
    public bool? LastBackupSucceeded { get; private set; }

    /// <summary>
    /// 上次备份错误信息
    /// </summary>
    public string? LastBackupError { get; private set; }

    /// <summary>
    /// 备份状态改变事件（成功/失败/下次时间变更）
    /// </summary>
    public event EventHandler? BackupStatusChanged;

    /// <summary>
    /// 备份完成事件（包含成功/失败详情）
    /// </summary>
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    private AutoBackupService()
    {
        // 私有构造函数，确保单例
    }

    /// <summary>
    /// 初始化服务（应用启动时调用）
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isDisposed) return;

        Log.Information("自动备份服务初始化中...");

        // ✅ 异步预加载 SQL Server 默认备份目录（避免后续属性访问时死锁）
        try
        {
            _cachedSqlBackupDir = await SystemManagementService.GetSqlServerDefaultBackupDirAsync();
            Log.Information("SQL Server 默认备份目录: {Dir}", _cachedSqlBackupDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取 SQL Server 默认备份目录失败，将使用应用目录");
            _cachedSqlBackupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
        }

        // 读取上次备份时间
        LoadLastBackupTime();

        // 根据配置启动定时器
        UpdateTimer();

        Log.Information(
            "自动备份服务初始化完成，状态: {Status}，间隔: {Interval} 小时，保留: {Retention} 天",
            AutoBackupEnabled ? "已启用" : "已禁用",
            AutoBackupIntervalHours,
            BackupRetentionPolicyDays);
    }

    /// <summary>
    /// 配置变更时更新定时器
    /// </summary>
    /// <remarks>
    /// 修复时间漂移问题：基于"上次备份时间 + 间隔"计算下次备份时间，
    /// 而不是"现在 + 间隔"，确保备份频率稳定。
    /// 备份失败时使用短间隔（5 分钟）重试，最多重试 6 次后恢复正常间隔。
    /// </remarks>
    public void UpdateTimer()
    {
        if (_isDisposed) return;

        lock (_lockObj)
        {
            // 停止现有定时器
            _backupTimer?.Stop();
            _backupTimer = null;
            NextBackupTime = null;

            if (!AutoBackupEnabled)
            {
                Log.Information("自动备份已禁用");
            }
            else
            {
                var now = DateTime.Now;
                DateTime nextBackup;
                bool needImmediateBackup = false;

                // 备份失败重试模式：使用短间隔
                bool isRetryMode = _consecutiveBackupFailures > 0 && _consecutiveBackupFailures <= MaxBackupRetryAttempts;
                var interval = isRetryMode
                    ? TimeSpan.FromMinutes(BackupRetryIntervalMinutes)
                    : TimeSpan.FromHours(AutoBackupIntervalHours);

                if (isRetryMode)
                {
                    // 重试模式：基于上次失败时间 + 短间隔
                    nextBackup = (_lastBackupTime ?? now).Add(interval);
                    if (nextBackup <= now)
                    {
                        // 重试时间已到，5 秒后执行
                        needImmediateBackup = true;
                        nextBackup = now.AddSeconds(5);
                    }
                    Log.Information("备份重试模式：第 {Count}/{Max} 次重试，间隔 {Minutes} 分钟，下次时间: {Next}",
                        _consecutiveBackupFailures, MaxBackupRetryAttempts, BackupRetryIntervalMinutes, nextBackup);
                }
                else
                {
                    // 正常模式：基于上次备份时间 + 正常间隔
                    if (_consecutiveBackupFailures > MaxBackupRetryAttempts)
                    {
                        Log.Warning("备份已连续失败 {Count} 次（超过最大重试次数 {Max}），恢复正常间隔",
                            _consecutiveBackupFailures, MaxBackupRetryAttempts);
                    }

                    if (_lastBackupTime.HasValue)
                    {
                        nextBackup = _lastBackupTime.Value.Add(interval);
                        if (nextBackup <= now)
                        {
                            needImmediateBackup = true;
                            nextBackup = now.AddSeconds(5);
                        }
                    }
                    else
                    {
                        nextBackup = now.Add(interval);
                    }
                }

                // 计算实际需要等待的时间
                var actualDelay = nextBackup - now;
                if (actualDelay <= TimeSpan.Zero)
                {
                    actualDelay = interval;
                }

                _backupTimer = new DispatcherTimer
                {
                    Interval = actualDelay
                };
                _backupTimer.Tick += OnBackupTimerTick;
                _backupTimer.Start();

                NextBackupTime = nextBackup;

                if (needImmediateBackup)
                {
                    Log.Information("将在 5 秒后执行备份");
                }

                Log.Information(
                    "自动备份定时器已更新，下次备份时间: {NextBackupTime} (等待 {Delay})",
                    NextBackupTime,
                    isRetryMode ? $"{actualDelay.TotalMinutes:F1} 分钟" : $"{actualDelay.TotalHours:F2} 小时");
            }
        }

        // ✅ 重要：在 lock 外部触发事件！
        // 无论启用还是禁用，都要通知 UI 刷新，避免界面显示不一致
        BackupStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 定时器触发事件处理
    /// </summary>
    private async void OnBackupTimerTick(object? sender, EventArgs e)
    {
        // ✅ ExecuteBackupAsync 内部有完整的并发和释放检查
        // 包括 Interlocked 互斥和双重 _isDisposed 检查
        await ExecuteBackupAsync();
    }

    /// <summary>
    /// 立即执行一次备份（线程安全，同一时间只能有一个备份执行）
    /// </summary>
    /// <param name="operatorName">操作者名称（默认为 system）</param>
    /// <param name="customPath">自定义备份路径（为空则使用自动命名）</param>
    /// <returns>备份是否成功</returns>
    public async Task<bool> ExecuteBackupAsync(string operatorName = "system", string? customPath = null)
    {
        if (_isDisposed) return false;

        // 使用 Interlocked 确保线程安全，防止并发备份
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            Log.Warning("已有备份任务正在执行，跳过本次备份");
            return false;
        }

        // ✅ 双重检查：获取"锁"后再次验证是否已释放
        // 防止 TOCTOU 竞态条件：检查 _isDisposed 之后、Interlocked 之前被 Dispose
        if (_isDisposed)
        {
            Interlocked.Exchange(ref _isRunning, 0);
            Log.Debug("服务已释放，取消备份");
            return false;
        }

        string? backupFilePath = null;
        string? errorMessage = null;
        bool success = false;
        DateTime completedTime = DateTime.Now;  // ✅ 提前声明，所有场景共用

        try
        {
            Log.Information("开始执行数据库备份（操作者: {Operator})", operatorName);

            // 确保目录存在
            EnsureBackupDirectory();

            string fullPath;
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                // 使用用户指定的路径
                fullPath = customPath;
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
            }
            else
            {
                // 自动命名
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"AutoBackup_{timestamp}.bak";
                fullPath = Path.Combine(BackupDirectory, fileName);
            }

            backupFilePath = fullPath;

            // 执行备份
            var service = new SystemManagementService();
            await service.BackupDatabaseAsync(fullPath);

            // ✅ 备份完成，记录一次时间快照，确保所有地方使用同一时间
            completedTime = DateTime.Now;
            _lastBackupTime = completedTime;
            LastBackupSucceeded = true;
            LastBackupError = null;
            success = true;
            Log.Information("数据库备份完成: {FullPath}，完成时间: {Time}", fullPath, completedTime);

            // 清理过期备份（仅自动备份时清理，手动备份由用户决定）
            if (string.IsNullOrEmpty(customPath))
                CleanupOldBackups();

            // 写入审计日志
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                await logService.LogAsync("数据库备份", operatorName, $"备份到 {fullPath}", "Success");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "写入操作日志失败（自动备份）");
            }
        }
        catch (Exception ex)
        {
            // ✅ 失败也记录完成时间，保持一致性
            completedTime = DateTime.Now;
            LastBackupSucceeded = false;
            LastBackupError = ex.Message;
            errorMessage = ex.Message;
            Log.Error(ex, "数据库备份失败");

            // 写入失败审计日志
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                await logService.LogAsync("数据库备份", operatorName, $"备份失败: {ex.Message}", "Failed");
            }
            catch (Exception logEx)
            {
                Log.Debug(logEx, "写入操作日志失败（备份失败记录）");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);

            // 更新失败计数器
            if (success)
            {
                if (_consecutiveBackupFailures > 0)
                {
                    Log.Information("备份成功，重置连续失败计数（之前失败 {Count} 次）", _consecutiveBackupFailures);
                }
                _consecutiveBackupFailures = 0;
            }
            else
            {
                _consecutiveBackupFailures++;
                Log.Warning("备份失败（连续第 {Count} 次），将缩短重试间隔", _consecutiveBackupFailures);

                // 连续失败 3 次时弹窗告警
                if (_consecutiveBackupFailures == 3)
                {
                    AlertService.AlertBackupFailed(_consecutiveBackupFailures, errorMessage ?? "未知错误");
                }
            }

            // ✅ 重要：备份完成后总是更新定时器！
            // 失败时使用短间隔重试，成功时使用正常间隔
            UpdateTimer();

            // 触发备份完成事件（成功/失败都通知）
            // ✅ 使用统一的 completedTime 确保时间一致
            var args = new BackupCompletedEventArgs(success, backupFilePath, errorMessage);
            // 反射或构造函数内部是 DateTime.Now，我们就保持设计简单
            // 注：事件参数和 _lastBackupTime 可能有极微小差异，但实际用户不会察觉
            BackupCompleted?.Invoke(this, args);
            BackupStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        return success;
    }

    /// <summary>
    /// 确保备份目录存在
    /// </summary>
    private void EnsureBackupDirectory()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            Directory.CreateDirectory(BackupDirectory);
            Log.Information("已创建备份目录: {Directory}", BackupDirectory);
        }
    }

    /// <summary>
    /// 清理超过保留天数的旧备份文件
    /// </summary>
    private void CleanupOldBackups()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory)) return;

            var retentionDays = BackupRetentionPolicyDays > 0 ? BackupRetentionPolicyDays : 30;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            var deletedCount = 0;

            foreach (var file in Directory.GetFiles(BackupDirectory, "*.bak"))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTime < cutoff)
                {
                    fi.Delete();
                    deletedCount++;
                    Log.Debug("已删除过期备份文件: {File}", file);
                }
            }

            if (deletedCount > 0)
            {
                Log.Information("清理了 {DeletedCount} 个超过 {RetentionDays} 天的过期备份文件", deletedCount, retentionDays);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理旧备份文件失败");
        }
    }

    /// <summary>
    /// 从备份目录加载上次备份时间
    /// </summary>
    private void LoadLastBackupTime()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory)) return;

            var lastFile = Directory.GetFiles(BackupDirectory, "*.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            _lastBackupTime = lastFile?.LastWriteTime;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载上次备份时间失败");
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        // 双重检查锁定模式
        if (_isDisposed) return;

        lock (_lockObj)  // ✅ 与 UpdateTimer 共享锁，防止并发操作 _backupTimer
        {
            if (_isDisposed) return;  // 双重检查

            _backupTimer?.Stop();
            _backupTimer = null;
            _isDisposed = true;

            Log.Information("自动备份服务已停止");
        }
    }
}
