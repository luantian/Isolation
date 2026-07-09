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

    // 通讯层
    private static ConnectionManager? _connectionManager;
    private static IConnectionFactory? _connectionFactory;
    private static IModbusPlcConnectionFactory? _modbusPlcConnectionFactory;

    // 安全层
    private static AuthService? _authService;
    private static UserService? _userService;
    private static RoleService? _roleService;
    private static MenuService? _menuService;

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
        _realtimeDataService = new RealtimeDataService(dbContext);
        _recipeService = new RecipeService(dbContext);

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

