using System.Threading;
using IsolationLeakage.App.Communication;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services.Security;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 应用服务定位器
/// </summary>
public static class AppServices
{
    private static AppDbContext? _dbContext;
    private static ProjectService? _projectService;
    private static UnitService? _unitService;
    private static TestObjectPathService? _pathService;
    private static MeasurementDeviceService? _deviceService;
    private static TestRecordService? _testRecordService;
    private static TestProcessDataService? _processDataService;
    private static TaskDownloadService? _taskDownloadService;
    private static RealtimeDataService? _realtimeDataService;
    private static RecipeService? _recipeService;
    private static MonitorVariableConfigService? _monitorVariableConfigService;

    // 通讯层
    private static ConnectionManager? _connectionManager;
    private static IConnectionFactory? _connectionFactory;
    private static IModbusPlcConnectionFactory? _modbusPlcConnectionFactory;

    // 安全层
    private static AuthService? _authService;
    private static UserService? _userService;
    private static RoleService? _roleService;
    private static MenuService? _menuService;

    // 优雅切换：服务代际计数器，每次 ReinitializeDataServices 递增
    // 后台操作可检查代际是否变化来判断是否需要重试
    private static long _serviceGeneration;

    // 优雅切换：读写锁，切换期间阻止新的 DB 操作
    private static readonly ReaderWriterLockSlim _switchLock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// 初始化服务（应用启动时调用）
    /// </summary>
    public static void Initialize(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        _projectService = new ProjectService(dbContext);
        _unitService = new UnitService(dbContext);
        _pathService = new TestObjectPathService(dbContext);
        _deviceService = new MeasurementDeviceService(dbContext);
        _testRecordService = new TestRecordService(dbContext);
        _processDataService = new TestProcessDataService(dbContext);
        _realtimeDataService = new RealtimeDataService();
        _recipeService = new RecipeService(dbContext);
        _monitorVariableConfigService = new MonitorVariableConfigService(dbContext);

        // 初始化通讯层
        _connectionFactory = new DeviceConnectionFactory();
        _modbusPlcConnectionFactory = new ModbusPlcConnectionFactory();
        _connectionManager = new ConnectionManager(_connectionFactory);

        // 初始化任务下载服务（需要连接管理器）
        _taskDownloadService = new TaskDownloadService(dbContext, _connectionManager);

        // 初始化安全层
        _authService = new AuthService(dbContext);
        _userService = new UserService(dbContext);
        _roleService = new RoleService(dbContext);
        _menuService = new MenuService(dbContext);
    }

    /// <summary>
    /// 仅替换数据库相关服务（故障切换时调用）。
    /// 不重建 ConnectionManager，保持 PLC 实时连接不中断。
    /// 使用写锁确保切换期间无 DB 操作在进行，避免 ObjectDisposedException。
    /// </summary>
    public static void ReinitializeDataServices(AppDbContext dbContext)
    {
        _switchLock.EnterWriteLock();
        try
        {
            // 先释放旧 DbContext（在替换前，确保不会有操作再用旧引用）
            var oldDbContext = _dbContext;

            _dbContext = dbContext;
            _projectService = new ProjectService(dbContext);
            _unitService = new UnitService(dbContext);
            _pathService = new TestObjectPathService(dbContext);
            _deviceService = new MeasurementDeviceService(dbContext);
            _testRecordService = new TestRecordService(dbContext);
            _processDataService = new TestProcessDataService(dbContext);
            _realtimeDataService = new RealtimeDataService();
            _recipeService = new RecipeService(dbContext);
            _monitorVariableConfigService = new MonitorVariableConfigService(dbContext);

            // 任务下载服务依赖 DbContext，需要重建（但保留 ConnectionManager）
            _taskDownloadService = new TaskDownloadService(dbContext, _connectionManager!);

            // 安全层
            _authService = new AuthService(dbContext);
            _userService = new UserService(dbContext);
            _roleService = new RoleService(dbContext);
            _menuService = new MenuService(dbContext);

            // 递增代际，通知后台操作服务已重建
            Interlocked.Increment(ref _serviceGeneration);

            // 在写锁外释放旧 DbContext（避免持锁期间做 I/O）
            oldDbContext?.Dispose();
        }
        finally
        {
            _switchLock.ExitWriteLock();
        }

        // 注意：不重建 _connectionManager、_connectionFactory、_modbusPlcConnectionFactory
        // PLC 实时连接在故障切换期间保持不断
    }

    /// <summary>
    /// 获取当前服务代际（用于后台操作检测切换）
    /// </summary>
    public static long ServiceGeneration => Interlocked.Read(ref _serviceGeneration);

    /// <summary>
    /// 后台操作应在开始 DB 操作前调用此方法，完成后调用 ExitDbOperation。
    /// 返回 false 表示获取锁超时（切换正在进行），操作应跳过或重试。
    /// 【H-11 注意】建议使用 EnterDbOperationScope() 代替，可自动释放（RAII 模式）。
    /// </summary>
    public static bool TryEnterDbOperation(int timeoutMs = 5000)
        => _switchLock.TryEnterReadLock(timeoutMs);

    /// <summary>
    /// 退出 DB 操作区域
    /// 【H-11 注意】如果使用 EnterDbOperationScope()，无需手动调用此方法。
    /// </summary>
    public static void ExitDbOperation()
        => _switchLock.ExitReadLock();

    /// <summary>
    /// 【H-11 修复】RAII 模式的 DB 操作作用域。
    /// 使用方式：using var scope = AppServices.EnterDbOperationScope();
    /// 作用域结束时自动释放读锁，即使发生异常也不会泄漏。
    /// </summary>
    /// <param name="timeoutMs">获取锁超时（毫秒），默认 5 秒</param>
    /// <returns>作用域对象。如果 Acquired 为 false，表示获取锁超时，不应执行 DB 操作。</returns>
    public static DbOperationScope EnterDbOperationScope(int timeoutMs = 5000)
        => new(_switchLock, timeoutMs);

    /// <summary>
    /// DB 操作作用域（IDisposable，RAII 模式自动释放读锁）
    /// </summary>
    public readonly struct DbOperationScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        /// <summary>
        /// 是否成功获取读锁。如果为 false，不应执行 DB 操作。
        /// </summary>
        public bool Acquired { get; }

        internal DbOperationScope(ReaderWriterLockSlim lockObj, int timeoutMs)
        {
            _lock = lockObj;
            Acquired = _lock.TryEnterReadLock(timeoutMs);
        }

        /// <summary>
        /// 释放读锁（仅当成功获取时才释放）
        /// </summary>
        public void Dispose()
        {
            if (Acquired)
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// 释放资源（应用退出时调用）
    /// </summary>
    public static void Shutdown()
    {
        // 停止自动备份服务
        AutoBackupService.Instance.Dispose();

        // 释放通讯层资源
        _connectionManager?.Dispose();
        _connectionManager = null;

        // 释放数据库上下文
        _dbContext?.Dispose();
        _dbContext = null;
    }

    public static AppDbContext DbContext
    {
        get
        {
            if (_dbContext == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _dbContext;
        }
    }

    public static ProjectService ProjectService
    {
        get
        {
            if (_projectService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _projectService;
        }
    }

    public static UnitService UnitService
    {
        get
        {
            if (_unitService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _unitService;
        }
    }

    public static TestObjectPathService PathService
    {
        get
        {
            if (_pathService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _pathService;
        }
    }

    public static MeasurementDeviceService DeviceService
    {
        get
        {
            if (_deviceService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _deviceService;
        }
    }

    public static TestRecordService TestRecordService
    {
        get
        {
            if (_testRecordService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _testRecordService;
        }
    }

    public static TestProcessDataService ProcessDataService
    {
        get
        {
            if (_processDataService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _processDataService;
        }
    }

    /// <summary>任务下载服务</summary>
    public static TaskDownloadService TaskDownloadService
    {
        get
        {
            if (_taskDownloadService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _taskDownloadService;
        }
    }

    /// <summary>实时监视曲线数据服务</summary>
    public static RealtimeDataService RealtimeDataService
    {
        get
        {
            if (_realtimeDataService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _realtimeDataService;
        }
    }

    /// <summary>试验配方管理服务</summary>
    public static RecipeService RecipeService
    {
        get
        {
            if (_recipeService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _recipeService;
        }
    }

    /// <summary>实时监视变量配置管理服务</summary>
    public static MonitorVariableConfigService MonitorVariableConfigService
    {
        get
        {
            if (_monitorVariableConfigService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _monitorVariableConfigService;
        }
    }

    // ================ 通讯层属性 ================

    /// <summary>设备连接工厂</summary>
    public static IConnectionFactory ConnectionFactory
    {
        get
        {
            if (_connectionFactory == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _connectionFactory;
        }
    }

    /// <summary>Modbus PLC 连接工厂</summary>
    public static IModbusPlcConnectionFactory ModbusPlcConnectionFactory
    {
        get
        {
            if (_modbusPlcConnectionFactory == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _modbusPlcConnectionFactory;
        }
    }

    /// <summary>连接管理器（设备连接跟踪、心跳、状态同步）</summary>
    public static ConnectionManager ConnectionManager
    {
        get
        {
            if (_connectionManager == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _connectionManager;
        }
    }

    // ================ 安全层属性 ================

    /// <summary>认证服务</summary>
    public static AuthService AuthService
    {
        get
        {
            if (_authService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _authService;
        }
    }

    /// <summary>用户服务</summary>
    public static UserService UserService
    {
        get
        {
            if (_userService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _userService;
        }
    }

    /// <summary>角色服务</summary>
    public static RoleService RoleService
    {
        get
        {
            if (_roleService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _roleService;
        }
    }

    /// <summary>菜单服务</summary>
    public static MenuService MenuService
    {
        get
        {
            if (_menuService == null)
                throw new InvalidOperationException("AppServices 未初始化，请先调用 Initialize 方法");
            return _menuService;
        }
    }
}

