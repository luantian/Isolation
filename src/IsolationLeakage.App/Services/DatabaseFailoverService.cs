using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Timers;
using Microsoft.Data.SqlClient;
using Serilog;
using IsolationLeakage.App.Configuration;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 数据库故障切换服务（主从自动切换）
/// 定时检测主库健康状态，主库故障时自动切换到从库，主库恢复后自动切回。
/// 单例模式，通过 Instance 访问。
/// </summary>
public sealed class DatabaseFailoverService : INotifyPropertyChanged, IDisposable
{
    #region 单例

    private static readonly Lazy<DatabaseFailoverService> _lazy =
        new(() => new DatabaseFailoverService());

    public static DatabaseFailoverService Instance => _lazy.Value;

    #endregion

    #region 枚举

    /// <summary>
    /// 数据库角色（当前连接的是哪个库）
    /// </summary>
    public enum DatabaseRole
    {
        /// <summary>主库</summary>
        Primary,
        /// <summary>从库</summary>
        Secondary,
    }

    /// <summary>
    /// 连接状态
    /// </summary>
    public enum DatabaseStatus
    {
        /// <summary>未启用故障切换</summary>
        Disabled,
        /// <summary>正常</summary>
        Normal,
        /// <summary>正在检测</summary>
        Checking,
        /// <summary>主库故障，正在切换</summary>
        FailingOver,
        /// <summary>从库运行中（主库已故障）</summary>
        OnSecondary,
        /// <summary>主库已恢复，等待切回</summary>
        WaitingFailback,
    }

    #endregion

    #region 事件

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<DatabaseRole>? RoleChanged;
    public event Action<DatabaseStatus>? StatusChanged;

    #endregion

    #region 字段

    private readonly object _lock = new();
    private int _healthCheckRunning; // 0=空闲 1=执行中，健康检查防重入（独立于 _lock，避免阻塞 I/O 期间长占 _lock）
    private System.Timers.Timer? _healthCheckTimer;
    private DatabaseRole _currentRole = DatabaseRole.Primary;
    private DatabaseStatus _status = DatabaseStatus.Disabled;
    private int _primaryFailureCount;
    private int _secondaryFailureCount;
    private int _failbackSuccessCount;
    private DateTime _lastFailoverTime = DateTime.MinValue;
    private bool _isRunning;
    private string _statusMessage = "故障切换未启用";

    // 配置
    private bool _enabled;
    private int _healthCheckIntervalSeconds = 15;
    private int _connectionTimeoutSeconds = 5;
    private int _failbackDelaySeconds = 60;
    private int _maxRetryBeforeFailover = 2;

    // 连接字符串（缓存，避免每次都从配置读）
    private string? _primaryConnectionString;
    private string? _secondaryConnectionString;

    // 从库/主库曾处于失败状态的标志：用于检测"从失败中恢复"的状态转换
    // 当从库从失败恢复时，即使角色没变，也需要重建 DbContext（旧连接已断）
    private bool _secondaryWasFailing;
    private bool _primaryWasFailing;

    #endregion

    #region 属性（供 UI 绑定）

    /// <summary>
    /// 故障切换功能是否启用
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        private set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// 当前连接的数据库角色
    /// </summary>
    public DatabaseRole CurrentRole
    {
        get => _currentRole;
        private set
        {
            if (_currentRole == value) return;
            _currentRole = value;
            OnPropertyChanged();
            RoleChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// 当前连接状态
    /// </summary>
    public DatabaseStatus CurrentStatus
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            StatusChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// 状态描述文本（供 UI 直接显示）
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// 最后一次故障切换时间
    /// </summary>
    public DateTime LastFailoverTime
    {
        get => _lastFailoverTime;
        private set => SetProperty(ref _lastFailoverTime, value);
    }

    /// <summary>
    /// 当前活跃连接字符串
    /// </summary>
    public string ActiveConnectionString
    {
        get
        {
            if (CurrentRole == DatabaseRole.Primary)
                return _primaryConnectionString ?? string.Empty;
            return _secondaryConnectionString ?? string.Empty;
        }
    }

    /// <summary>
    /// 当前连接的服务器显示名
    /// </summary>
    public string CurrentServerDisplay
    {
        get
        {
            try
            {
                var connStr = ActiveConnectionString;
                if (string.IsNullOrEmpty(connStr)) return "未配置";
                var builder = new SqlConnectionStringBuilder(connStr);
                return builder.DataSource;
            }
            catch
            {
                return "未知";
            }
        }
    }

    #endregion

    #region 初始化

    private DatabaseFailoverService() { }

    /// <summary>
    /// 初始化故障切换服务（从配置加载参数）
    /// </summary>
    public void Initialize()
    {
        // 读取主库连接字符串
        _primaryConnectionString = AppConfiguration.GetConnectionString("DefaultConnection");

        // 读取从库连接字符串
        _secondaryConnectionString = AppConfiguration.GetConnectionString("SecondaryConnection");

        // 读取故障切换配置
        var failoverSection = AppConfiguration.Instance.GetSection("Failover");
        _enabled = bool.TryParse(failoverSection?.GetSection("Enabled")?.Value, out var en) && en;
        _healthCheckIntervalSeconds = int.TryParse(failoverSection?.GetSection("HealthCheckIntervalSeconds")?.Value, out var hc) ? hc : 15;
        _connectionTimeoutSeconds = int.TryParse(failoverSection?.GetSection("ConnectionTimeoutSeconds")?.Value, out var ct) ? ct : 5;
        _failbackDelaySeconds = int.TryParse(failoverSection?.GetSection("FailbackDelaySeconds")?.Value, out var fd) ? fd : 60;
        _maxRetryBeforeFailover = int.TryParse(failoverSection?.GetSection("MaxRetryBeforeFailover")?.Value, out var mr) ? mr : 2;

        IsEnabled = _enabled;

        if (!_enabled)
        {
            CurrentStatus = DatabaseStatus.Disabled;
            StatusMessage = "故障切换未启用";
            Log.Information("数据库故障切换未启用");
            return;
        }

        if (string.IsNullOrWhiteSpace(_secondaryConnectionString))
        {
            CurrentStatus = DatabaseStatus.Disabled;
            StatusMessage = "故障切换已启用，但未配置从库连接";
            Log.Warning("故障切换已启用，但 SecondaryConnection 连接字符串为空");
            return;
        }

        CurrentRole = DatabaseRole.Primary;
        CurrentStatus = DatabaseStatus.Normal;
        StatusMessage = $"主库运行中 ({CurrentServerDisplay})";
        _primaryFailureCount = 0;
        _secondaryFailureCount = 0;
        _failbackSuccessCount = 0;

        Log.Information(
            "数据库故障切换已初始化：主库={Primary}, 从库={Secondary}, 检测间隔={Interval}秒, 超时={Timeout}秒",
            ExtractServer(_primaryConnectionString),
            ExtractServer(_secondaryConnectionString),
            _healthCheckIntervalSeconds,
            _connectionTimeoutSeconds);
    }

    #endregion

    #region 启停

    /// <summary>
    /// 启动健康检查定时器
    /// </summary>
    public void Start()
    {
        if (!_enabled || string.IsNullOrWhiteSpace(_secondaryConnectionString))
        {
            Log.Information("故障切换条件不满足，不启动健康检查");
            return;
        }

        lock (_lock)
        {
            if (_isRunning) return;

            _healthCheckTimer?.Dispose();
            _healthCheckTimer = new System.Timers.Timer(_healthCheckIntervalSeconds * 1000);
            _healthCheckTimer.Elapsed += OnHealthCheckElapsed;
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();
            _isRunning = true;

            Log.Information("数据库健康检查已启动，间隔 {Interval} 秒", _healthCheckIntervalSeconds);
        }
    }

    /// <summary>
    /// 停止健康检查
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _healthCheckTimer?.Stop();
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
            _isRunning = false;
            Log.Information("数据库健康检查已停止");
        }
    }

    #endregion

    #region 健康检查

    private void OnHealthCheckElapsed(object? sender, ElapsedEventArgs e)
    {
        // 防重入用独立标志，而非持 _lock 整个检查周期：
        // 连接测试是阻塞 I/O（每次最多 _connectionTimeoutSeconds 秒），若期间持 _lock，
        // UI 线程调用 Stop/ReloadConfiguration/ForceSwitchTo 会被阻塞卡顿。
        if (Interlocked.CompareExchange(ref _healthCheckRunning, 1, 0) != 0) return;
        try
        {
            PerformHealthCheck();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "健康检查发生异常");
        }
        finally
        {
            Interlocked.Exchange(ref _healthCheckRunning, 0);
        }
    }

    /// <summary>
    /// 执行健康检查：阻塞式连接测试在锁外执行，仅状态判定与字段变更在锁内。
    /// 为避免持锁期间触发 PropertyChanged（可能通过 WPF Binding Dispatcher.Invoke 造成死锁），
    /// 在锁内收集需要更新的状态，锁外再统一赋值触发 UI 通知。
    /// </summary>
    private void PerformHealthCheck()
    {
        // 短暂持锁读取快照
        DatabaseRole role;
        string? primaryConn, secondaryConn;
        lock (_lock)
        {
            if (!_enabled || !_isRunning) return;
            if (string.IsNullOrWhiteSpace(_secondaryConnectionString)) return;
            role = _currentRole;
            primaryConn = _primaryConnectionString;
            secondaryConn = _secondaryConnectionString;
        }

        // 锁外设置 Checking 状态（此时不持锁，不会死锁）
        CurrentStatus = DatabaseStatus.Checking;

        // 在锁外准备要设置的状态值
        DatabaseStatus? newStatus = null;
        string? newMessage = null;
        bool doFailover = false;
        bool doFailback = false;
        bool doRebuildConnection = false;
        bool doAlertBothDown = false;
        bool failoverSecondaryOk = false;

        if (role == DatabaseRole.Primary)
        {
            // 锁外阻塞测试
            bool primaryOk = TestConnection(primaryConn);
            // 主库异常时预探从库（供可能的切换判定用）
            bool secondaryOk = primaryOk || TestConnection(secondaryConn);

            lock (_lock)
            {
                if (!_isRunning || _currentRole != DatabaseRole.Primary) return;
                ApplyPrimaryHealthLocked(primaryOk, secondaryOk, out newStatus, out newMessage, out doFailover, out failoverSecondaryOk, out doRebuildConnection);
            }
        }
        else
        {
            // 锁外阻塞测试：从库是否正常 + 主库是否恢复
            bool secondaryOk = TestConnection(secondaryConn);
            bool primaryOk = TestConnection(primaryConn);

            lock (_lock)
            {
                if (!_isRunning || _currentRole != DatabaseRole.Secondary) return;
                ApplySecondaryHealthLocked(secondaryOk, primaryOk, out newStatus, out newMessage, out doFailback, out doRebuildConnection, out doAlertBothDown);
            }
        }

        // 锁外赋值触发 PropertyChanged（避免持锁期间 Dispatcher.Invoke 死锁）
        if (newStatus.HasValue) CurrentStatus = newStatus.Value;
        if (newMessage != null) StatusMessage = newMessage;

        // 【H-4 修复】双库皆挂告警（从库角色下检测到主从都不可用）
        if (doAlertBothDown)
        {
            AlertService.AlertBothDatabasesDown();
        }

        // 锁外执行切换（内部会自行设置状态）
        if (doFailover) FailoverToSecondary(failoverSecondaryOk);
        if (doFailback) FailbackToPrimary(true);

        // 连接恢复重建：当数据库从失败中恢复但角色未变时，重建 DbContext（旧连接已断）
        // 不触发完整的 Failover/Failback 流程，仅重建连接
        if (doRebuildConnection && !doFailover && !doFailback)
        {
            Log.Information("数据库连接恢复，重建 DbContext");
            RaiseDbConnectionChanged();

            // 触发数据缓冲区刷新（补写缓冲数据）
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                try
                {
                    await DataBufferService.Instance.FlushAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "连接恢复后刷新数据缓冲区失败");
                }
            });
        }
    }

    /// <summary>
    /// 在 _lock 内判定主库健康状态，返回需要更新的状态值（不在锁内触发 PropertyChanged）
    /// 修复：当主库从失败状态恢复时（仍在主库角色），触发 DbContext 重建。
    /// </summary>
    private void ApplyPrimaryHealthLocked(
        bool primaryOk, bool secondaryOk,
        out DatabaseStatus? newStatus, out string? newMessage,
        out bool doFailover, out bool failoverSecondaryOk,
        out bool doRebuildConnection)
    {
        newStatus = null;
        newMessage = null;
        doFailover = false;
        failoverSecondaryOk = false;
        doRebuildConnection = false;

        if (primaryOk)
        {
            // 主库当前可用
            // 如果之前主库处于失败状态，现在恢复了 → 需要重建 DbContext
            if (_primaryWasFailing)
            {
                Log.Information("主库已从异常中恢复，触发 DbContext 重建");
                _primaryWasFailing = false;
                doRebuildConnection = true;
            }

            _primaryFailureCount = 0;
            newStatus = DatabaseStatus.Normal;
            newMessage = $"主库运行中 ({CurrentServerDisplay})";
        }
        else
        {
            _primaryFailureCount++;
            _primaryWasFailing = true;
            Log.Warning("主库连接失败（第 {Count}/{Max} 次）", _primaryFailureCount, _maxRetryBeforeFailover);

            if (_primaryFailureCount >= _maxRetryBeforeFailover)
            {
                // 标记在锁外执行切换
                doFailover = true;
                failoverSecondaryOk = secondaryOk;
            }
            else
            {
                newStatus = DatabaseStatus.Checking;
                newMessage = $"主库连接异常 ({_primaryFailureCount}/{_maxRetryBeforeFailover})";
            }
        }
    }

    /// <summary>
    /// 在 _lock 内检查从库健康状态，返回需要更新的状态值（不在锁内触发 PropertyChanged）
    /// 修复：当从库从失败状态恢复时（即使主库仍挂），触发 DbContext 重建，避免使用断开的旧连接。
    /// 修复：当从库异常且主库也异常时，更新状态并标记告警（H-4）。
    /// </summary>
    private void ApplySecondaryHealthLocked(
        bool secondaryOk, bool primaryRecovered,
        out DatabaseStatus? newStatus, out string? newMessage,
        out bool doFailback, out bool doRebuildConnection,
        out bool doAlertBothDown)
    {
        newStatus = null;
        newMessage = null;
        doFailback = false;
        doRebuildConnection = false;
        doAlertBothDown = false;

        if (!secondaryOk)
        {
            _secondaryFailureCount++;
            Log.Warning("从库连接失败（第 {Count} 次）", _secondaryFailureCount);

            if (primaryRecovered)
            {
                Log.Information("主库已恢复，从库也异常，立即切回主库");
                doFailback = true;
                newMessage = "主库已恢复，正在切回...";
            }
            else
            {
                // 【H-4 修复】主从都挂 → 更新状态 + 标记告警
                newStatus = DatabaseStatus.Checking;
                newMessage = $"主库和从库均无法连接！从库异常({_secondaryFailureCount}次)";
                doAlertBothDown = true;
                Log.Error("主库和从库均无法连接（从库运行中角色下）");
            }

            // 标记从库处于失败状态，下次恢复时触发连接重建
            _secondaryWasFailing = true;
            return;
        }

        // 从库当前可用
        // 如果之前从库处于失败状态，现在恢复了 → 需要重建 DbContext（旧连接已断）
        if (_secondaryWasFailing)
        {
            Log.Information("从库已从异常中恢复，触发 DbContext 重建（主库状态: {Primary}）",
                primaryRecovered ? "已恢复" : "仍挂");
            _secondaryWasFailing = false;
            doRebuildConnection = true;
        }

        _secondaryFailureCount = 0;

        if (primaryRecovered)
        {
            _failbackSuccessCount++;
            if (_failbackSuccessCount >= _maxRetryBeforeFailover)
            {
                var elapsed = (DateTime.Now - _lastFailoverTime).TotalSeconds;
                if (elapsed >= _failbackDelaySeconds)
                {
                    Log.Information("主库已恢复且稳定（连续成功 {Count} 次，距上次切换 {Elapsed} 秒），准备切回",
                        _failbackSuccessCount, elapsed);
                    doFailback = true;
                }
                else
                {
                    newStatus = DatabaseStatus.WaitingFailback;
                    newMessage = $"主库已恢复，等待 {Math.Max(0, (int)(_failbackDelaySeconds - elapsed))} 秒后切回";
                    Log.Debug("主库已恢复但距上次切换时间不足（{Elapsed}秒），继续等待", (int)elapsed);
                }
            }
            else
            {
                newStatus = DatabaseStatus.WaitingFailback;
                newMessage = $"主库已恢复，确认中 ({_failbackSuccessCount}/{_maxRetryBeforeFailover})";
            }
        }
        else
        {
            _failbackSuccessCount = 0;
            newStatus = DatabaseStatus.OnSecondary;
            newMessage = $"从库运行中 ({CurrentServerDisplay})";
        }
    }

    #endregion

    #region 切换逻辑

    /// <summary>
    /// 故障切换到从库
    /// 【H-5 修复】添加 try/catch 确保异常时状态回滚到 Checking，不会卡在 FailingOver
    /// </summary>
    /// <param name="secondaryOk">从库连接测试结果（由健康检查在锁外预探）</param>
    private void FailoverToSecondary(bool secondaryOk)
    {
        Log.Warning("主库连续 {Count} 次连接失败，执行故障切换到从库", _primaryFailureCount);

        CurrentStatus = DatabaseStatus.FailingOver;
        StatusMessage = "正在切换到从库...";

        try
        {
            // 验证从库可以连接（使用预探结果，避免在 _lock 内做阻塞 I/O）
            if (!secondaryOk)
            {
                Log.Error("从库也无法连接！故障切换失败，继续使用主库");
                StatusMessage = "主库和从库均无法连接！";
                CurrentStatus = DatabaseStatus.Checking;
                _primaryFailureCount = 0; // 重置，下个周期重试

                // 通知操作员：主从都挂了（严重事件，强制显示）
                AlertService.AlertBothDatabasesDown();

                return;
            }

            // 执行切换
            CurrentRole = DatabaseRole.Secondary;
            _primaryFailureCount = 0;
            _primaryWasFailing = false;
            _secondaryWasFailing = false;
            _failbackSuccessCount = 0;
            LastFailoverTime = DateTime.Now;
            CurrentStatus = DatabaseStatus.OnSecondary;
            StatusMessage = $"从库运行中 ({CurrentServerDisplay})";

            Log.Warning("已切换到从库: {Server}", ExtractServer(_secondaryConnectionString));

            // 通知操作员（弹窗 + 声音）
            AlertService.AlertFailover(
                ExtractServer(_primaryConnectionString),
                ExtractServer(_secondaryConnectionString));

            // 通知外部重建 DbContext
            RaiseDbConnectionChanged();

            // 触发数据缓冲区刷新（DB 连接恢复后补写缓冲数据）
            Task.Run(async () =>
            {
                // 等几秒让 DbContext 重建完成
                await Task.Delay(3000);
                try
                {
                    await DataBufferService.Instance.FlushAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "故障切换后刷新数据缓冲区失败");
                }
            });
        }
        catch (Exception ex)
        {
            // 【H-5 修复】异常时回滚状态，确保不会卡在 FailingOver
            Log.Error(ex, "故障切换到从库过程中发生异常，回滚状态");
            CurrentStatus = DatabaseStatus.Checking;
            StatusMessage = $"切换异常: {ex.Message}";
        }
    }

    /// <summary>
    /// 故障切回主库
    /// 【H-5 修复】添加 try/catch 确保异常时状态回滚到 OnSecondary，不会卡在 FailingOver
    /// </summary>
    /// <param name="primaryOk">主库连接测试结果（由健康检查在锁外预探）</param>
    private void FailbackToPrimary(bool primaryOk)
    {
        Log.Information("执行故障切回，从从库切换回主库");

        CurrentStatus = DatabaseStatus.FailingOver;
        StatusMessage = "正在切回主库...";

        try
        {
            // 再次确认主库可用（使用预探结果，避免在 _lock 内做阻塞 I/O）
            if (!primaryOk)
            {
                Log.Warning("切回时主库不可用，取消切回");
                CurrentStatus = DatabaseStatus.OnSecondary;
                StatusMessage = $"从库运行中 ({CurrentServerDisplay})";
                _failbackSuccessCount = 0;
                return;
            }

            // 执行切回
            CurrentRole = DatabaseRole.Primary;
            _primaryFailureCount = 0;
            _primaryWasFailing = false;
            _secondaryWasFailing = false;
            _failbackSuccessCount = 0;
            LastFailoverTime = DateTime.Now;
            CurrentStatus = DatabaseStatus.Normal;
            StatusMessage = $"主库运行中 ({CurrentServerDisplay})";

            Log.Information("已切回主库: {Server}", ExtractServer(_primaryConnectionString));

            // 通知操作员（弹窗 + 声音）
            AlertService.AlertFailback(ExtractServer(_primaryConnectionString));

            // 通知外部重建 DbContext
            RaiseDbConnectionChanged();

            // 触发数据缓冲区刷新
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                try
                {
                    await DataBufferService.Instance.FlushAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "故障切回后刷新数据缓冲区失败");
                }
            });
        }
        catch (Exception ex)
        {
            // 【H-5 修复】异常时回滚状态，确保不会卡在 FailingOver
            Log.Error(ex, "故障切回主库过程中发生异常，回滚状态");
            CurrentStatus = DatabaseStatus.OnSecondary;
            StatusMessage = $"切回异常: {ex.Message}";
        }
    }

    /// <summary>
    /// 数据库连接变更事件（通知外部重建 DbContext）
    /// </summary>
    public event Action? DbConnectionChanged;

    private void RaiseDbConnectionChanged()
    {
        OnPropertyChanged(nameof(ActiveConnectionString));
        OnPropertyChanged(nameof(CurrentServerDisplay));
        DbConnectionChanged?.Invoke();
    }

    #endregion

    #region 连接测试

    /// <summary>
    /// 测试数据库连接是否可用。
    /// 先尝试连接目标数据库；如果目标库不存在（首次启动），回退到连接 master 测试实例可用性。
    /// </summary>
    private bool TestConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = _connectionTimeoutSeconds,
            };
            var targetDb = builder.InitialCatalog;

            using var connection = new SqlConnection(builder.ToString());
            connection.Open();

            // 验证目标数据库确实可查询（不只是实例活着）
            using var cmd = new SqlCommand($"SELECT 1 FROM [{targetDb}].sys.tables", connection);
            cmd.ExecuteScalar();

            return true;
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            // 4060 = 目标数据库不存在（首次启动时正常），回退测 master
            return TestConnectionMaster(connectionString);
        }
        catch (Exception ex)
        {
            Log.Debug("连接测试失败: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 回退方案：连接 master 测试 SQL Server 实例是否可用
    /// </summary>
    private bool TestConnectionMaster(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = _connectionTimeoutSeconds,
                InitialCatalog = "master"
            };

            using var connection = new SqlConnection(builder.ToString());
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 辅助方法

    /// <summary>
    /// 从连接字符串中提取服务器名
    /// </summary>
    private static string ExtractServer(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "未配置";
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return builder.DataSource;
        }
        catch
        {
            return "未知";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion

    #region 公开方法

    /// <summary>
    /// 手动强制切换到指定角色（用于启动时自动检测场景）
    /// </summary>
    public void ForceSwitchTo(DatabaseRole role)
    {
        lock (_lock)
        {
            CurrentRole = role;
            _primaryFailureCount = 0;
            _failbackSuccessCount = 0;
            LastFailoverTime = DateTime.Now;

            if (role == DatabaseRole.Primary)
            {
                CurrentStatus = DatabaseStatus.Normal;
                StatusMessage = $"主库运行中 ({CurrentServerDisplay})";
            }
            else
            {
                CurrentStatus = DatabaseStatus.OnSecondary;
                StatusMessage = $"从库运行中 ({CurrentServerDisplay})";
            }

            RaiseDbConnectionChanged();
            Log.Information("已手动强制切换到{Role}: {Server}", role, CurrentServerDisplay);
        }
    }

    /// <summary>
    /// 测试指定连接字符串是否可以连接
    /// </summary>
    public bool TestConnectionString(string connectionString)
    {
        return TestConnection(connectionString);
    }

    /// <summary>
    /// 重新加载配置（在 SqlServerConfigDialog 保存新配置后调用）。
    /// 不强制重置当前角色——如果正在从库运行则保持。
    /// </summary>
    public void ReloadConfiguration()
    {
        var previousRole = CurrentRole;

        Stop();
        _primaryConnectionString = AppConfiguration.GetConnectionString("DefaultConnection");
        _secondaryConnectionString = AppConfiguration.GetConnectionString("SecondaryConnection");

        var failoverSection = AppConfiguration.Instance.GetSection("Failover");
        _enabled = bool.TryParse(failoverSection?.GetSection("Enabled")?.Value, out var en) && en;
        _healthCheckIntervalSeconds = int.TryParse(failoverSection?.GetSection("HealthCheckIntervalSeconds")?.Value, out var hc) ? hc : 15;
        _connectionTimeoutSeconds = int.TryParse(failoverSection?.GetSection("ConnectionTimeoutSeconds")?.Value, out var ct) ? ct : 5;
        _failbackDelaySeconds = int.TryParse(failoverSection?.GetSection("FailbackDelaySeconds")?.Value, out var fd) ? fd : 60;
        _maxRetryBeforeFailover = int.TryParse(failoverSection?.GetSection("MaxRetryBeforeFailover")?.Value, out var mr) ? mr : 2;

        IsEnabled = _enabled;

        if (!_enabled || string.IsNullOrWhiteSpace(_secondaryConnectionString))
        {
            CurrentRole = DatabaseRole.Primary;
            CurrentStatus = DatabaseStatus.Disabled;
            StatusMessage = _enabled ? "故障切换已启用，但未配置从库连接" : "故障切换未启用";
            return;
        }

        // 保持之前的角色不变（如果之前就在从库运行，继续留在从库）
        if (previousRole == DatabaseRole.Secondary)
        {
            CurrentStatus = DatabaseStatus.OnSecondary;
            StatusMessage = $"从库运行中 ({CurrentServerDisplay})";
        }
        else
        {
            CurrentStatus = DatabaseStatus.Normal;
            StatusMessage = $"主库运行中 ({CurrentServerDisplay})";
        }

        // 重置计数器
        _primaryFailureCount = 0;
        _secondaryFailureCount = 0;
        _failbackSuccessCount = 0;

        // 重新启动健康检查
        Start();
        Log.Information("故障切换配置已重新加载，当前角色: {Role}", CurrentRole);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Stop();
    }

    #endregion
}
