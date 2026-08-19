using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Implementations;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;
using OxyPlot.Axes;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 实时监视变量（支持 UI 编辑）
/// 支持 Modbus（寄存器地址）和 Siemens S7（西门子地址格式）两种协议
/// </summary>
public sealed class MonitorVariable : ObservableObject
{
    private string _variableName = string.Empty;
    private int _registerAddress; // Modbus 使用
    private string _siemensAddress = string.Empty; // 西门子 S7 使用，如 DB15.DBD0
    private string _dataType = "double";
    private string _unit = string.Empty;
    private string? _curveChannel;
    private double _minDisplay;
    private double _maxDisplay;
    private string _currentValue = "-";
    private string _updatedAt = "-";
    private string _status = "待连接";

    /// <summary>所属装置编码（多装置模式下只读展示；单装置为 DEFAULT，显示为空）</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>装置列显示文本（DEFAULT 不显示）</summary>
    public string DeviceDisplay => string.IsNullOrEmpty(DeviceCode) || DeviceCode == "DEFAULT" ? string.Empty : DeviceCode;

    public string VariableName
    {
        get => _variableName;
        set => SetProperty(ref _variableName, value);
    }
    /// <summary>Modbus 寄存器地址</summary>
    public int RegisterAddress
    {
        get => _registerAddress;
        set => SetProperty(ref _registerAddress, value);
    }
    /// <summary>西门子 S7 地址格式，如 DB15.DBD0</summary>
    public string SiemensAddress
    {
        get => _siemensAddress;
        set => SetProperty(ref _siemensAddress, value);
    }
    public string DataType
    {
        get => _dataType;
        set => SetProperty(ref _dataType, value);
    }
    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }
    public string? CurveChannel
    {
        get => _curveChannel;
        set => SetProperty(ref _curveChannel, value);
    }
    public double MinDisplay
    {
        get => _minDisplay;
        set => SetProperty(ref _minDisplay, value);
    }
    public double MaxDisplay
    {
        get => _maxDisplay;
        set => SetProperty(ref _maxDisplay, value);
    }
    public string CurrentValue
    {
        get => _currentValue;
        set => SetProperty(ref _currentValue, value);
    }
    public string UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 关联的趋势通道（供实时变量表显示曲线颜色、并通过勾选控制曲线显隐）。
    /// 由 ViewModel 在同步通道时赋值；不参与配置持久化。
    /// </summary>
    private Controls.TrendChannel? _channel;
    public Controls.TrendChannel? Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, value);
    }

    /// <summary>转为配置对象</summary>
    public PlcVariableConfig ToConfig() => new()
    {
        VariableCode = VariableName.Replace(" ", "_").ToUpper(),
        VariableName = VariableName,
        RegisterAddress = RegisterAddress,
        SiemensAddress = SiemensAddress,
        DataType = DataType,
        Unit = Unit,
        CurveChannel = CurveChannel,
        MinDisplay = MinDisplay,
        MaxDisplay = MaxDisplay,
    };

    /// <summary>从配置对象创建</summary>
    public static MonitorVariable FromConfig(PlcVariableConfig cfg) => new()
    {
        VariableName = cfg.VariableName,
        RegisterAddress = cfg.RegisterAddress,
        SiemensAddress = cfg.SiemensAddress,
        DataType = cfg.DataType,
        Unit = cfg.Unit,
        CurveChannel = cfg.CurveChannel,
        MinDisplay = cfg.MinDisplay,
        MaxDisplay = cfg.MaxDisplay,
    };
}

/// <summary>
/// 实时监视的装置选项（多设备模式：同一时刻只采集/显示所选的一台装置——独占切换模型）
/// </summary>
public sealed class PlcDeviceItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string DeviceCode { get; init; } = string.Empty;

    private string _displayName = string.Empty;
    /// <summary>显示名（装置编号 + IP，台账 IP 覆盖后更新）</summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    private string _status = "未连接";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

/// <summary>
/// 实时监视视图模型
/// </summary>
public sealed partial class RealtimeMonitorViewModel : ViewModelBase, IRefreshable, IDisposable
{
    // 使用 System.Timers.Timer（后台线程），PLC 读数据不阻塞 UI
    private readonly System.Timers.Timer _timer;
    private readonly Dispatcher _uiDispatcher;
    private RealtimeDataService? _realtimeDataService;
    private MonitorVariableConfigService? _variableConfigService;
    private string? _currentSessionCode;
    private CancellationTokenSource? _readCts;
    private bool _disposed;
    private int _tickCount;
    // Tick 重入标志：0=空闲 1=读取中。读取慢于采样周期时跳过本次，避免并发访问同一 PLC 连接。
    private int _tickRunning;
    private const int SaveInterval = 100; // 每 100 次 tick 保存一次曲线

    // PLC 自动重连：连续读取失败次数阈值（每装置独立计数）
    private const int AutoReconnectThreshold = 3; // 连续失败 3 次后自动重连

    // ===== 多设备运行时：每装置一份连接/配置/失败计数，单装置旧配置归一化为 DEFAULT =====
    private sealed class DeviceRuntime
    {
        public required string DeviceCode { get; init; }
        public required PlcConnectionConfig ConnectionConfig { get; init; }
        public required List<PlcVariableConfig> RegisterConfigs { get; set; }
        public int SampleIntervalMs { get; init; } = 1000;
        public IModbusPlcConnection? Connection { get; set; }
        public int ConsecutiveReadFailures { get; set; }
        /// <summary>装置是否参与本轮读取（连接失败达到阈值后置 false，重连成功恢复）</summary>
        public bool IsAlive { get; set; } = true;
        /// <summary>图例/曲线名短前缀（如 [D1]），不含"压/流/温"字样以免污染曲线分组关键字</summary>
        public string ShortLabel { get; set; } = string.Empty;
    }

    private readonly List<DeviceRuntime> _devices = [];
    private readonly Dictionary<string, DeviceRuntime> _deviceByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RealtimeVariableItem> _variableItemByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonitorVariable> _monitorVarByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeasurementDevice> _ledgerIpByCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否多装置模式（plc-registers.json 配置了 Devices 且非单一 DEFAULT）。
    /// true 时变量配置以 JSON 为准（MonitorVariableConfig 表无装置维度），编辑表只读。</summary>
    private bool _multiDevice;

    /// <summary>多装置模式（控制变量编辑表只读/工具栏隐藏，XAML 绑定用；配置启动时加载，运行期不变）</summary>
    public bool IsMultiDeviceMode => _multiDevice;

    /// <summary>单装置模式（XAML 绑定用）</summary>
    public bool IsSingleDeviceMode => !_multiDevice;

    /// <summary>单装置模式的变量配置视图（多装置模式下指向主装置）——供变量表格/探测/持久化使用</summary>
    private List<PlcVariableConfig> _registerConfigs = [];

    /// <summary>主装置（= 当前所选装置；未选择时第一台）</summary>
    private DeviceRuntime? PrimaryDevice => _devices.FirstOrDefault(d =>
            string.Equals(d.DeviceCode, SelectedPlcDevice?.DeviceCode, StringComparison.OrdinalIgnoreCase))
        ?? _devices.FirstOrDefault();

    /// <summary>通道键：多装置模式 "DeviceCode:VariableCode"（防两台装置同名变量冲突），单装置保持原 VariableCode（兼容既有数据）</summary>
    private string ChannelKey(string deviceCode, string variableCode)
        => _multiDevice ? $"{deviceCode}:{variableCode}" : variableCode;

    /// <summary>
    /// 变量的显示单位：压力类通道一律按 kPa 显示（存储/PLC 原始值仍为 MPa，喂曲线时换算）。
    /// 兼容存量数据库与旧 json 中 Unit="MPa" 的压力变量（不改库，运行期统一显示 kPa）。
    /// </summary>
    private static string DisplayUnitFor(PlcVariableConfig cfg)
        => Helpers.PressureUnitConverter.IsPressureChannel(cfg.CurveChannel)
            ? Helpers.PressureUnitConverter.DisplayUnit
            : cfg.Unit;

    // TrendChart 数据源（支持批量操作，减少事件触发）— 5 通道
    public BulkObservableCollection<double> PressurePoints { get; } = [];
    public BulkObservableCollection<double> FlowPoints { get; } = [];
    public BulkObservableCollection<double> TempPoints { get; } = [];
    public BulkObservableCollection<double> Flow2Points { get; } = [];
    public BulkObservableCollection<double> Pressure2Points { get; } = [];
    // X 轴：单调递增的采样序号（与各通道等长、同步裁剪），使曲线持续向右累加滚动
    public BulkObservableCollection<double> TimeAxisPoints { get; } = [];
    // 累计采样计数（即使旧点被裁剪也持续增长）
    private long _sampleSeq;
    // 监视开始时间（用于计算相对秒数，避免使用 DateTimeAxis.ToDouble 的大数字）
    private DateTime _monitorStartTime = DateTime.Now;

    // 状态属性
    [ObservableProperty]
    private string _connectionState = "未连接";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isMonitoring;

    /// <summary>本监视会话是否发生过仿真降级（用于落库时在试验记录备注中显式标注仿真数据，防止被当作真实测量结果）。</summary>
    private bool _usedSimulationFallback;

    [ObservableProperty]
    private int _sampleIntervalMs = 1000;

    partial void OnSampleIntervalMsChanged(int value)
    {
        OnPropertyChanged(nameof(CurveInfoText));
        // 动态调整定时器间隔（System.Timers.Timer.Interval 是 double 类型，单位毫秒）
        if (_timer != null) _timer.Interval = value;
    }

    /// <summary>波形显示时长（秒），默认 600 秒（10 分钟）</summary>
    [ObservableProperty]
    private int _displayDurationSeconds = 600;

    partial void OnDisplayDurationSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(CurveInfoText));
    }

    /// <summary>视口是否自动跟随最新数据点（勾选=跟随最新；取消=停在用户拖拽/缩放的位置）。
    /// 双向绑定到三张 TrendChart：用户右键平移或滚轮缩放时图表会把它置 false，取消勾选。</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>应用显示时长命令（确认按钮）：重新开启自动跟随，使三张图按新窗口对齐到最新数据。
    /// 不裁剪任何采集数据——全部数据始终保留在曲线中，取消“自动”后可自由向左拖拽查看历史。</summary>
    [RelayCommand]
    private void ApplyDisplayDuration()
    {
        // 置 true 触发图表的 AutoScroll 变更回调，按当前显示时长立即重新对齐视口（停止监视时也生效）。
        AutoScroll = true;
        Log.Information("[实时监视] 显示时长已应用：显示窗口 {Seconds}s，恢复自动跟随", DisplayDurationSeconds);
    }

    /// <summary>趋势曲线标题描述文本</summary>
    public string CurveInfoText => $"采样周期 {SampleIntervalMs}ms · 显示窗口 {DisplayDurationSeconds}s · 已采集 {_fullSampleTimes.Count} 点(全部保存)";

    [ObservableProperty]
    private string _sessionInfo = "未开始监视";

    [ObservableProperty]
    private string _plcIpAddress = "127.0.0.1";

    // ============ 试验对象选择 ============
    public ObservableCollection<Project> AvailableProjects { get; } = [];
    public ObservableCollection<Unit> AvailableUnits { get; } = [];
    public ObservableCollection<TestObjectPathNode> AvailableObjects { get; } = [];

    // 为 true 时抑制“选择变更→重载子级并清空子选择”的联动。
    // 用于页面刷新时按编码恢复 项目→机组→对象 的选择链，避免联动把已选内容清空。
    private bool _suppressSelectionCascade;

    [ObservableProperty]
    private Project? _selectedProject;

    partial void OnSelectedProjectChanged(Project? value)
    {
        if (GuardMonitoringSelection(value, _monitoringLockProject, v => SelectedProject = v)) return;

        if (_suppressSelectionCascade) return;
        _ = LoadUnitsAsync(value);
        SelectedUnit = null;
        SelectedObject = null;
    }

    [ObservableProperty]
    private Unit? _selectedUnit;

    partial void OnSelectedUnitChanged(Unit? value)
    {
        if (GuardMonitoringSelection(value, _monitoringLockUnit, v => SelectedUnit = v)) return;

        if (_suppressSelectionCascade) return;
        _ = LoadObjectsAsync(value);
        SelectedObject = null;
    }

    [ObservableProperty]
    private TestObjectPathNode? _selectedObject;

    partial void OnSelectedObjectChanged(TestObjectPathNode? value)
    {
        // 试验对象自身无级联副作用，仅做监视中锁定拦截
        GuardMonitoringSelection(value, _monitoringLockObject, v => SelectedObject = v);
    }

    // ============ 监视中锁定试验对象选择 ============
    // 记录归属在 StartMonitoringAsync 时已定格；监视期间切换"项目/机组/试验对象"会造成
    // 界面显示与实际记录归属不一致，故锁定三个下拉（与 SelectedPlcDevice 的守卫同策略）。
    private Project? _monitoringLockProject;
    private Unit? _monitoringLockUnit;
    private TestObjectPathNode? _monitoringLockObject;

    /// <summary>
    /// 监视中拦截选择切换：回弹到锁定值并提示；非监视状态（或刷新恢复期间）放行。
    /// 返回 true 表示已拦截，调用方应直接 return。
    /// </summary>
    private bool GuardMonitoringSelection<T>(T? newValue, T? lockedValue, Action<T?> revert) where T : class
    {
        if (!IsMonitoring || _suppressSelectionCascade || ReferenceEquals(newValue, lockedValue)) return false;

        MessageBox.Show(
            "监视进行中，试验对象已锁定（记录归属以开始监视时的选择为准）。\n请先停止监视，再切换项目/机组/试验对象。",
            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);

        // 回弹到锁定值：抑制级联，避免递归触发守卫与误清空下游选择
        _suppressSelectionCascade = true;
        try { revert(lockedValue); }
        finally { _suppressSelectionCascade = false; }
        return true;
    }

    // ============ 测量装置选择 ============
    /// <summary>可选的测量装置（来自台账，仅启用状态）</summary>
    public ObservableCollection<MeasurementDevice> AvailableDevices { get; } = [];

    [ObservableProperty]
    private MeasurementDevice? _selectedDevice;

    /// <summary>参与实时采集的 PLC 装置勾选列表（来自 plc-registers.json 的 Devices）</summary>
    public ObservableCollection<PlcDeviceItem> PlcDevices { get; } = [];

    /// <summary>可编辑的寄存器变量列表（用于 UI 配置）</summary>
    public ObservableCollection<MonitorVariable> MonitorVariables { get; } = [];

    // ===== 全量历史缓冲（带大小限制，防止长时间运行内存耗尽）=====
    /// <summary>全量采样时间（每个 tick 追加一次，超过限制时定期清理已保存的数据）</summary>
    private readonly List<DateTime> _fullSampleTimes = [];
    /// <summary>全量通道数据：变量编码 → 该通道所有采样值（超过限制时定期清理已保存的数据）</summary>
    private readonly Dictionary<string, List<double>> _fullChannelData = new();
    /// <summary>内存缓冲区最大保留点数（约 24 小时数据，1 秒间隔）</summary>
    private const int MaxBufferPoints = 86400;
    /// <summary>上次自动保存时的采样序号</summary>
    private long _lastAutoSaveSeq;
    /// <summary>持久化并发控制：自动保存忙时跳过，停止/关闭时等待其空闲后再存最终版。</summary>
    private readonly System.Threading.SemaphoreSlim _persistLock = new(1, 1);

    public RealtimeMonitorViewModel()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        // 加载 PLC 寄存器配置
        LoadPlcConfig();
        Log.Information("[实时监视] 初始化完成，寄存器数={Count}, IP={IP}", _registerConfigs.Count, PlcIpAddress);

        // 单装置模式：应用启动时从数据库加载已保存的变量配置（多装置模式内部自动跳过）。
        // 监视开始时不再重复加载——变量表格编辑"立即生效"，开始监视时再从数据库重建
        // 会把未保存的修改静默回滚（表格所见 ≠ 实际采集配置）。
        _variableConfigService = AppServices.MonitorVariableConfigService;
        _ = LoadVariablesFromDatabaseAsync();

        // 加载试验对象选择数据
        _ = LoadProjectsAsync();

        // 加载测量装置列表
        _ = LoadDevicesAsync();

        // 使用 System.Timers.Timer（后台线程运行）
        // PLC 读数据在后台线程完成，只有更新 UI 时才切回 UI 线程
        // 彻底解决 PLC 通信延迟阻塞 UI 的问题
        _timer = new System.Timers.Timer(SampleIntervalMs);
        _timer.Elapsed += async (_, _) =>
        {
            // async void 语义：此处抛出的异常会被运行时静默吞掉（表现为监视假死且无日志），
            // 必须兜底捕获，保证定时器链路永不中断。
            try
            {
                if (!_disposed && _isMonitoring) await TickAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[实时监视] 采样定时器回调异常");
            }
        };
    }

    /// <summary>
    /// 页面激活时刷新数据（IRefreshable 接口）
    /// </summary>
    public async Task RefreshAsync()
    {
        Log.Information("[实时监视] 页面激活，开始刷新数据...");

        // 记住当前选择（按编码），刷新后恢复——避免切换页面回来后项目/机组/对象被清空。
        var pCode = SelectedProject?.Code;
        var uCode = SelectedUnit?.Code;
        var oCode = SelectedObject?.Code;

        // 抑制联动：手动按顺序重载并恢复整条选择链，联动的“清空子选择”会破坏恢复。
        _suppressSelectionCascade = true;
        try
        {
            await LoadProjectsAsync();
            SelectedProject = pCode == null ? null : AvailableProjects.FirstOrDefault(p => p.Code == pCode);

            await LoadUnitsAsync(SelectedProject);
            SelectedUnit = uCode == null ? null : AvailableUnits.FirstOrDefault(u => u.Code == uCode);

            await LoadObjectsAsync(SelectedUnit);
            SelectedObject = oCode == null ? null : AvailableObjects.FirstOrDefault(o => o.Code == oCode);
        }
        finally
        {
            _suppressSelectionCascade = false;
        }

        await LoadDevicesAsync();
        Log.Information("[实时监视] 数据刷新完成：Projects={Projects}, Devices={Devices}, 已恢复选择 P={P} U={U} O={O}",
            AvailableProjects.Count, AvailableDevices.Count, pCode ?? "-", uCode ?? "-", oCode ?? "-");
    }

    /// <summary>
    /// 加载 PLC 寄存器配置（已归一化为 Devices 列表：旧单装置格式包装为 DEFAULT）
    /// </summary>
    private void LoadPlcConfig()
    {
        try
        {
            var cfg = AppConfiguration.GetPlcRegisters();
            var devices = cfg.Devices ?? [];
            _multiDevice = devices.Count > 1 || !devices.Exists(d => d.DeviceCode.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));

            // 构建装置运行时 + 勾选列表
            _devices.Clear();
            _deviceByCode.Clear();
            PlcDevices.Clear();
            for (int i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                var runtime = new DeviceRuntime
                {
                    DeviceCode = dev.DeviceCode,
                    ConnectionConfig = dev.Connection,
                    RegisterConfigs = dev.Variables,
                    SampleIntervalMs = dev.SampleIntervalMs > 0 ? dev.SampleIntervalMs : 1000,
                    ShortLabel = $"D{i + 1}",
                };
                _devices.Add(runtime);
                _deviceByCode[dev.DeviceCode] = runtime;
                var plcItem = new PlcDeviceItem
                {
                    DeviceCode = dev.DeviceCode,
                    DisplayName = _multiDevice ? $"{runtime.ShortLabel} {dev.DeviceCode}（{dev.Connection.IpAddress}）" : dev.Connection.IpAddress,
                };
                plcItem.PropertyChanged += OnPlcDeviceItemChanged;
                PlcDevices.Add(plcItem);
            }

            // 初始选中第一台装置（直接赋字段：初始化时不触发切换副作用，末尾统一 SyncChannelsFromDevices）
            _selectedPlcDevice = PlcDevices.FirstOrDefault();

            var primary = PrimaryDevice;
            _registerConfigs = primary?.RegisterConfigs ?? [];
            PlcIpAddress = primary?.ConnectionConfig.IpAddress ?? "127.0.0.1";
            SampleIntervalMs = primary?.SampleIntervalMs ?? 1000;

            // 构建变量表格：单装置=可编辑（MonitorVariables + DB）；多装置=整体只读展示全部装置的变量
            MonitorVariables.Clear();
            Variables.Clear();
            _variableItemByCode.Clear();
            _monitorVarByKey.Clear();
            foreach (var dev in _devices)
            {
                foreach (var vc in dev.RegisterConfigs)
                {
                    var displayUnit = DisplayUnitFor(vc);
                    var mv = MonitorVariable.FromConfig(vc);
                    mv.DeviceCode = dev.DeviceCode;
                    mv.Unit = displayUnit;
                    MonitorVariables.Add(mv);

                    var key = ChannelKey(dev.DeviceCode, vc.VariableCode);
                    _monitorVarByKey[key] = mv;

                    var item = new RealtimeVariableItem
                    {
                        VariableCode = key,
                        DeviceCode = dev.DeviceCode,
                        VariableName = vc.VariableName,
                        CurrentValue = "-",
                        Unit = displayUnit,
                        Channel = string.IsNullOrEmpty(vc.SiemensAddress)
                            ? $"Reg {vc.RegisterAddress} ({vc.DataType})"
                            : $"{vc.SiemensAddress} ({vc.DataType})",
                        UpdatedAt = "-",
                        Status = "待连接",
                        CurveChannel = vc.CurveChannel,
                        MinDisplay = vc.MinDisplay,
                        MaxDisplay = vc.MaxDisplay,
                    };
                    Variables.Add(item);
                    _variableItemByCode[key] = item;
                }
            }

            // 初始化曲线显示范围
            foreach (var dev in _devices)
            {
                foreach (var vc in dev.RegisterConfigs.Where(v => v.CurveChannel != null))
                {
                    UpdateChannelRange(vc.CurveChannel!, vc.MinDisplay, vc.MaxDisplay, vc.Unit);
                }
            }

            // 构建动态趋势通道（每个变量一条曲线 + 图例项；多装置叠加在同一组图表）
            SyncChannelsFromDevices();
        }
        catch (Exception ex)
        {
            ConnectionState = $"配置加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 从数据库加载变量配置（替代硬编码）。
    /// 仅单装置模式：MonitorVariableConfig 表无装置维度，多装置模式的变量以 plc-registers.json 为准。
    /// </summary>
    private async Task LoadVariablesFromDatabaseAsync()
    {
        try
        {
            if (_multiDevice)
            {
                Log.Information("[实时监视] 多装置模式：跳过数据库变量加载，变量配置以 plc-registers.json 为准");
                return;
            }

            if (_variableConfigService == null)
            {
                Log.Warning("[实时监视] 变量配置服务未初始化，使用默认配置");
                return;
            }

            var configs = await _variableConfigService.GetEnabledVariablesAsync();
            if (configs.Count == 0)
            {
                Log.Warning("[实时监视] 数据库中没有变量配置，使用默认配置");
                return;
            }

            // 在 UI 线程更新 MonitorVariables 与 DEFAULT 装置运行时
            await _uiDispatcher.InvokeAsync(() =>
            {
                MonitorVariables.Clear();
                _monitorVarByKey.Clear();

                foreach (var config in configs)
                {
                    var mv = new MonitorVariable
                    {
                        VariableName = config.VariableName,
                        RegisterAddress = config.RegisterAddress,
                        SiemensAddress = config.SiemensAddress,
                        DataType = config.DataType,
                        // 压力类通道一律按 kPa 显示（存量 DB 的 MPa 行不改库，运行期统一显示单位）
                        Unit = Helpers.PressureUnitConverter.IsPressureChannel(config.CurveChannel)
                            ? Helpers.PressureUnitConverter.DisplayUnit
                            : config.Unit,
                        CurveChannel = config.CurveChannel,
                        MinDisplay = config.MinDisplay,
                        MaxDisplay = config.MaxDisplay,
                        DeviceCode = string.Empty, // 单装置 DEFAULT，装置列不显示
                    };
                    MonitorVariables.Add(mv);
                    _monitorVarByKey[mv.ToConfig().VariableCode] = mv;
                }

                // 初始化曲线显示范围
                foreach (var config in configs.Where(c => c.CurveChannel != null))
                {
                    UpdateChannelRange(config.CurveChannel!, config.MinDisplay, config.MaxDisplay, config.Unit);
                }

                // 回写 DEFAULT 装置配置并重建通道 + 只读变量表
                SyncChannelsFromVariables();
                RebuildReadonlyVariables();
            });

            Log.Information("[实时监视] 从数据库加载了 {Count} 个变量配置", configs.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] 从数据库加载变量配置失败");
        }
    }

    /// <summary>
    /// 加载项目列表
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            Log.Information("[实时监视] 正在加载项目列表...");

            var projects = await context.Projects
                .Where(p => p.Status == EnabledStatus.Enabled)
                .OrderBy(p => p.Code)
                .ToListAsync();

            Log.Information("[实时监视] 加载项目：{Count} 个", projects.Count);
            foreach (var p in projects)
            {
                Log.Information("[实时监视]   - {Code}: {Name}", p.Code, p.Name);
            }

            _uiDispatcher.Invoke(() =>
            {
                AvailableProjects.Clear();
                foreach (var p in projects) AvailableProjects.Add(p);
            });
        }
        catch (Exception ex)
        {
            Log.Warning("[实时监视] 加载项目失败：{Error}", ex.Message);
            ConnectionState = $"加载项目失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 加载机组列表（按项目过滤）
    /// </summary>
    private async Task LoadUnitsAsync(Project? project)
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var query = context.Units
                .Where(u => u.Status == EnabledStatus.Enabled);

            if (project != null)
                query = query.Where(u => u.ProjectCode == project.Code);

            var units = await query.OrderBy(u => u.Code).ToListAsync();

            _uiDispatcher.Invoke(() =>
            {
                AvailableUnits.Clear();
                foreach (var u in units) AvailableUnits.Add(u);
            });
        }
        catch (Exception ex)
        {
            Log.Warning("[实时监视] 加载机组失败：{Error}", ex.Message);
        }
    }

    /// <summary>
    /// 加载试验对象列表（按机组过滤）
    /// </summary>
    private async Task LoadObjectsAsync(Unit? unit)
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var query = context.TestObjectPathNodes
                .Where(n => n.Status == EnabledStatus.Enabled);

            if (unit != null)
                query = query.Where(n => n.UnitCode == unit.Code);

            var objects = await query.OrderBy(n => n.Code).ToListAsync();

            _uiDispatcher.Invoke(() =>
            {
                AvailableObjects.Clear();
                foreach (var o in objects) AvailableObjects.Add(o);
            });
        }
        catch (Exception ex)
        {
            Log.Warning("[实时监视] 加载试验对象失败：{Error}", ex.Message);
        }
    }

    /// <summary>
    /// 加载测量装置列表（仅启用状态，按编号排序）。
    /// 供实时监视选择记录所属装置，避免写死不存在的编号导致外键失败。
    /// 同时用台账 IP 覆盖各 PLC 装置运行时的连接 IP（台账与 json 不一致时以台账为准）。
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var devices = await context.MeasurementDevices
                .AsNoTracking()
                .Where(d => d.EnabledStatus == EnabledStatus.Enabled && d.DeviceCode != "未指定")
                .OrderBy(d => d.DeviceCode)
                .ToListAsync();

            _uiDispatcher.Invoke(() =>
            {
                // 台账 IP 映射：DeviceCode → Ip（多装置模式下覆盖 json 里的连接地址）
                _ledgerIpByCode.Clear();
                foreach (var d in devices)
                {
                    if (!string.IsNullOrWhiteSpace(d.Ip))
                        _ledgerIpByCode[d.DeviceCode] = d;
                }

                foreach (var runtime in _devices)
                {
                    if (_ledgerIpByCode.TryGetValue(runtime.DeviceCode, out var ledger) &&
                        !string.IsNullOrWhiteSpace(ledger.Ip) &&
                        runtime.ConnectionConfig.IpAddress != ledger.Ip)
                    {
                        Log.Information("[实时监视] 装置 {Device} 连接 IP 以台账为准：{Old} → {New}",
                            runtime.DeviceCode, runtime.ConnectionConfig.IpAddress, ledger.Ip);
                        runtime.ConnectionConfig.IpAddress = ledger.Ip;
                    }
                }

                // 刷新勾选列表显示名（含覆盖后的 IP）
                foreach (var item in PlcDevices)
                {
                    var runtime = _deviceByCode.GetValueOrDefault(item.DeviceCode);
                    if (runtime == null) continue;
                    item.DisplayName = _multiDevice
                        ? $"{runtime.ShortLabel} {runtime.DeviceCode}（{runtime.ConnectionConfig.IpAddress}）"
                        : runtime.ConnectionConfig.IpAddress;
                }

                // 主装置 IP 同步到地址框
                if (PrimaryDevice is { } primary)
                {
                    PlcIpAddress = primary.ConnectionConfig.IpAddress;
                }

                // 保留当前选择（按编号），刷新后尽量恢复
                var previousCode = SelectedDevice?.DeviceCode;

                AvailableDevices.Clear();
                foreach (var d in devices) AvailableDevices.Add(d);

                SelectedDevice = AvailableDevices.FirstOrDefault(d => d.DeviceCode == previousCode)
                                 ?? AvailableDevices.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            Log.Warning("[实时监视] 加载测量装置失败：{Error}", ex.Message);
        }
    }

    /// <summary>
    /// 保存寄存器配置（同步到数据库）。多装置模式下变量由 plc-registers.json 管理，不可界面编辑。
    /// </summary>
    public async void SaveConfig()
    {
        try
        {
            if (_multiDevice)
            {
                ConnectionState = "多装置模式：变量由 plc-registers.json 管理，不可在界面编辑";
                return;
            }

            if (_variableConfigService == null)
            {
                ConnectionState = "变量配置服务未初始化";
                return;
            }

            _registerConfigs = MonitorVariables.Select(mv => mv.ToConfig()).ToList();

            // 获取数据库中现有的所有配置
            var existingConfigs = await _variableConfigService.GetAllVariablesAsync();

            // 同步每个变量到数据库
            foreach (var mv in MonitorVariables)
            {
                var existing = existingConfigs.FirstOrDefault(c =>
                    c.VariableName == mv.VariableName);

                if (existing != null)
                {
                    // 更新现有变量
                    existing.RegisterAddress = mv.RegisterAddress;
                    existing.SiemensAddress = mv.SiemensAddress;
                    existing.DataType = mv.DataType;
                    existing.Unit = mv.Unit;
                    existing.CurveChannel = mv.CurveChannel;
                    existing.MinDisplay = mv.MinDisplay;
                    existing.MaxDisplay = mv.MaxDisplay;
                    await _variableConfigService.UpdateAsync(existing);
                }
                else
                {
                    // 添加新变量
                    var newConfig = new Models.Database.MonitorVariableConfig
                    {
                        VariableName = mv.VariableName,
                        RegisterAddress = mv.RegisterAddress,
                        SiemensAddress = mv.SiemensAddress,
                        DataType = mv.DataType,
                        Unit = mv.Unit,
                        CurveChannel = mv.CurveChannel,
                        MinDisplay = mv.MinDisplay,
                        MaxDisplay = mv.MaxDisplay,
                        SortOrder = MonitorVariables.IndexOf(mv) + 1,
                        IsEnabled = true,
                    };
                    await _variableConfigService.CreateAsync(newConfig);
                }
            }

            // 删除数据库中多余但 UI 中已删除的变量
            var uiVariableNames = MonitorVariables.Select(mv => mv.VariableName).ToList();
            foreach (var config in existingConfigs)
            {
                if (!uiVariableNames.Contains(config.VariableName))
                {
                    await _variableConfigService.DeleteAsync(config.Id);
                }
            }

            // 同步动态通道 + 重建只读变量列表
            SyncChannelsFromVariables();
            RebuildReadonlyVariables();

            // 重新初始化曲线范围
            foreach (var cfg in _registerConfigs.Where(v => v.CurveChannel != null))
            {
                UpdateChannelRange(cfg.CurveChannel!, cfg.MinDisplay, cfg.MaxDisplay);
            }

            // 更新定时器间隔（System.Timers.Timer 单位是毫秒，double 类型）
            _timer.Interval = SampleIntervalMs;

            ConnectionState = $"✅ 已保存 {MonitorVariables.Count} 个变量配置到数据库";
        }
        catch (Exception ex)
        {
            ConnectionState = $"保存配置失败：{ex.Message}";
            Log.Error(ex, "[实时监视] 保存配置失败");
        }
    }

    /// <summary>
    /// 保存 PLC 地址（只更新主装置的运行时配置；PLC 地址由 plc-registers.json 统一管理，不落盘）
    /// </summary>
    private void SavePlcIp()
    {
        var primary = PrimaryDevice;
        if (primary == null) return;

        primary.ConnectionConfig.IpAddress = PlcIpAddress;

        // 同步勾选列表显示名
        var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == primary.DeviceCode);
        if (item != null)
        {
            item.DisplayName = _multiDevice
                ? $"{primary.ShortLabel} {primary.DeviceCode}（{PlcIpAddress}）"
                : PlcIpAddress;
        }

        ConnectionState = $"✅ PLC 地址已更新：{PlcIpAddress}（重启后恢复为配置文件中的地址）";
    }

    /// <summary>
    /// 将当前配置保存到 plc-registers.json
    /// 【已弃用】变量配置现在保存到数据库，不再使用此方法
    /// </summary>
    [Obsolete("变量配置现在保存到数据库，不再使用 JSON 文件")]
    private void SavePlcConfigToJson()
    {
        // 此方法已弃用，保留仅为兼容
        Log.Warning("[实时监视] SavePlcConfigToJson 已弃用，变量配置应保存到数据库");
    }

    /// <summary>
    /// 添加新变量（保存到数据库）。多装置模式下变量由 plc-registers.json 管理。
    /// </summary>
    public async void AddVariable()
    {
        try
        {
            if (_multiDevice)
            {
                ConnectionState = "多装置模式：变量由 plc-registers.json 管理，不可在界面添加";
                return;
            }

            if (_variableConfigService == null)
            {
                ConnectionState = "变量配置服务未初始化";
                return;
            }

            // 确保变量名唯一
            int suffix = 1;
            string baseName = "新变量";
            string newName = baseName;
            while (MonitorVariables.Any(mv => mv.VariableName == newName))
            {
                suffix++;
                newName = $"{baseName}{suffix}";
            }

            // 计算下一个排序顺序
            var maxSort = MonitorVariables.Count > 0
                ? MonitorVariables.Max(mv => mv.RegisterAddress) + 4
                : 0;

            // 创建数据库配置对象
            var config = new Models.Database.MonitorVariableConfig
            {
                VariableName = newName,
                RegisterAddress = maxSort,
                SiemensAddress = $"DB15.{maxSort}",
                DataType = "real",
                Unit = "",
                MinDisplay = 0,
                MaxDisplay = 100,
                SortOrder = MonitorVariables.Count + 1,
                IsEnabled = true,
            };

            // 保存到数据库
            config = await _variableConfigService.CreateAsync(config);

            // 同步到 UI 列表
            var variable = new MonitorVariable
            {
                VariableName = config.VariableName,
                RegisterAddress = config.RegisterAddress,
                SiemensAddress = config.SiemensAddress,
                DataType = config.DataType,
                Unit = config.Unit,
                MinDisplay = config.MinDisplay,
                MaxDisplay = config.MaxDisplay,
            };
            MonitorVariables.Add(variable);

            // 同步通道和只读变量列表
            SyncChannelsFromVariables();
            RebuildReadonlyVariables();

            ConnectionState = $"✅ 已添加变量「{newName}」";
        }
        catch (Exception ex)
        {
            ConnectionState = $"添加变量失败：{ex.Message}";
            Log.Error(ex, "[实时监视] 添加变量失败");
        }
    }

    /// <summary>
    /// 删除变量（从数据库中删除）。多装置模式下变量由 plc-registers.json 管理。
    /// </summary>
    public async void RemoveVariable(MonitorVariable? variable)
    {
        if (variable == null) return;

        try
        {
            if (_multiDevice)
            {
                ConnectionState = "多装置模式：变量由 plc-registers.json 管理，不可在界面删除";
                return;
            }

            if (_variableConfigService == null)
            {
                ConnectionState = "变量配置服务未初始化";
                return;
            }

            // 从数据库中查找并删除
            var configs = await _variableConfigService.GetAllVariablesAsync();
            var config = configs.FirstOrDefault(c =>
                c.VariableName == variable.VariableName &&
                c.RegisterAddress == variable.RegisterAddress);

            if (config != null)
            {
                await _variableConfigService.DeleteAsync(config.Id);
            }

            // 从 UI 列表中移除
            MonitorVariables.Remove(variable);
            SyncChannelsFromVariables();
            RebuildReadonlyVariables();

            ConnectionState = $"✅ 已删除变量「{variable.VariableName}」";
        }
        catch (Exception ex)
        {
            ConnectionState = $"删除变量失败：{ex.Message}";
            Log.Error(ex, "[实时监视] 删除变量失败");
        }
    }

    /// <summary>重建只读变量列表（当前值表格，全部装置），保留已有当前值。</summary>
    private void RebuildReadonlyVariables()
    {
        var snapshot = _variableItemByCode.ToDictionary(
            kv => kv.Key, kv => (kv.Value.CurrentValue, kv.Value.UpdatedAt), StringComparer.OrdinalIgnoreCase);
        Variables.Clear();
        _variableItemByCode.Clear();

        foreach (var dev in _devices)
        {
            foreach (var cfg in dev.RegisterConfigs)
            {
                var key = ChannelKey(dev.DeviceCode, cfg.VariableCode);
                snapshot.TryGetValue(key, out var prev);
                // 根据配置显示对应地址：优先西门子地址，其次 Modbus 寄存器地址
                string channelDisplay = !string.IsNullOrEmpty(cfg.SiemensAddress)
                    ? $"{cfg.SiemensAddress} ({cfg.DataType})"
                    : $"Reg {cfg.RegisterAddress} ({cfg.DataType})";

                var item = new RealtimeVariableItem
                {
                    VariableCode = key,
                    DeviceCode = dev.DeviceCode,
                    VariableName = cfg.VariableName,
                    CurrentValue = prev.CurrentValue ?? "-",
                    Unit = DisplayUnitFor(cfg),
                    Channel = channelDisplay,
                    UpdatedAt = prev.UpdatedAt ?? "-",
                    Status = "待连接",
                    CurveChannel = cfg.CurveChannel,
                    MinDisplay = cfg.MinDisplay,
                    MaxDisplay = cfg.MaxDisplay,
                };
                Variables.Add(item);
                _variableItemByCode[key] = item;
            }
        }
    }

    /// <summary>
    /// 更新曲线通道范围（压力通道按 kPa 显示）。
    /// 兼容两种配置刻度：配置 Unit 为 kPa（新格式，刻度已是千帕）直接用；
    /// 旧格式/DB 存量 Unit=MPa（刻度为兆帕）时 ×1000。
    /// </summary>
    private void UpdateChannelRange(string channel, double min, double max, string? configUnit = null)
    {
        bool isPressure = channel is "Pressure" or "Pressure2";
        // 已是 kPa 刻度则不再换算
        double scale = isPressure && !Helpers.PressureUnitConverter.IsKPa(configUnit) ? 1000.0 : 1.0;

        switch (channel)
        {
            case "Pressure": PressureMin = min * scale; PressureMax = max * scale; break;
            case "Flow": FlowMin = min; FlowMax = max; break;
            case "Temp": TempMin = min; TempMax = max; break;
            case "Flow2": Flow2Min = min; Flow2Max = max; break;
            case "Pressure2": Pressure2Min = min * scale; Pressure2Max = max * scale; break;
        }
    }

    // 曲线范围属性
    [ObservableProperty] private double _pressureMin;
    [ObservableProperty] private double _pressureMax = 1;
    [ObservableProperty] private double _flowMin;
    [ObservableProperty] private double _flowMax = 1;
    [ObservableProperty] private double _tempMin;
    [ObservableProperty] private double _tempMax = 1;
    [ObservableProperty] private double _flow2Min;
    [ObservableProperty] private double _flow2Max = 1;
    [ObservableProperty] private double _pressure2Min;
    [ObservableProperty] private double _pressure2Max = 1;

    // 变量列表
    public ObservableCollection<RealtimeVariableItem> Variables { get; } = [];

    /// <summary>
    /// 动态趋势通道：每个监控变量一条曲线 + 一个图例项（绑定到图例；曲线按分组分到下面三个图表）。
    /// </summary>
    public ObservableCollection<Controls.TrendChannel> Channels { get; } = [];

    /// <summary>压力分组通道（压力P1/P2）——绑定“压力”图表。</summary>
    public ObservableCollection<Controls.TrendChannel> PressureChannels { get; } = [];
    /// <summary>温度分组通道——绑定“温度”图表。</summary>
    public ObservableCollection<Controls.TrendChannel> TempChannels { get; } = [];
    /// <summary>流量分组通道（流量M1/M2）+ 其他未归类通道——绑定“流量”图表。</summary>
    public ObservableCollection<Controls.TrendChannel> FlowChannels { get; } = [];

    /// <summary>按曲线通道标识把通道归入 压力/温度/流量 三组之一。</summary>
    private ObservableCollection<Controls.TrendChannel> GroupCollectionFor(string? curveChannel)
    {
        var s = (curveChannel ?? string.Empty).ToLowerInvariant();
        if (s.Contains("pressure") || s.Contains("压力")) return PressureChannels;
        if (s.Contains("temp") || s.Contains("温度")) return TempChannels;
        // 流量及未归类通道都放到流量图表（兜底，避免自定义通道丢失）
        return FlowChannels;
    }

    // 变量(按编码) → 通道，便于 tick 时按编码喂数据、增删时保留已有曲线
    private readonly Dictionary<string, Controls.TrendChannel> _channelByCode = new();

    // 通道配色板（循环使用）
    private static readonly System.Windows.Media.Color[] _palette =
    {
        System.Windows.Media.Color.FromRgb(0x07, 0x58, 0xD8), // 蓝
        System.Windows.Media.Color.FromRgb(0x12, 0xA3, 0x66), // 绿
        System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16), // 橙
        System.Windows.Media.Color.FromRgb(0x0E, 0xA5, 0xE9), // 青
        System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6), // 紫
        System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48), // 红
        System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04), // 金
        System.Windows.Media.Color.FromRgb(0x0D, 0x94, 0x88), // 蓝绿
    };

    /// <summary>
    /// 单装置模式：根据当前 MonitorVariables（编辑表）同步动态通道与读取配置。
    /// 多装置模式下编辑表只读（配置由 plc-registers.json 管理），不走此路径。
    /// </summary>
    private void SyncChannelsFromVariables()
    {
        if (_multiDevice) return;

        // 重建读取配置，使 tick 立即读取新变量（不必先点保存）
        _registerConfigs = MonitorVariables.Select(mv => mv.ToConfig()).ToList();
        var primary = _deviceByCode.TryGetValue("DEFAULT", out var d) ? d : _devices.FirstOrDefault();
        if (primary != null) primary.RegisterConfigs = _registerConfigs;

        SyncChannelsFromDevices();
    }

    /// <summary>
    /// 按所选装置的变量配置同步动态通道：新变量立即出现在图例/曲线；
    /// 切换装置或已删除的变量对应通道被移除；已存在的保留曲线数据。
    /// 多装置模式下通道名加短前缀（如 "[D1] 压力P1"）标识装置来源。
    /// </summary>
    private void SyncChannelsFromDevices()
    {
        // 独占模型：只显示/采集当前所选装置（未选择时默认第一台）
        var selectedCode = SelectedPlcDevice?.DeviceCode;
        if (string.IsNullOrEmpty(selectedCode) && _devices.Count > 0)
        {
            selectedCode = _devices[0].DeviceCode;
        }

        var wantedKeys = new HashSet<string>();
        int paletteIdx = 0;

        foreach (var dev in _devices)
        {
            if (!string.Equals(dev.DeviceCode, selectedCode, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var cfg in dev.RegisterConfigs)
            {
                var key = ChannelKey(dev.DeviceCode, cfg.VariableCode);
                wantedKeys.Add(key);
                var displayName = _multiDevice ? $"{dev.ShortLabel} {cfg.VariableName}" : cfg.VariableName;
                var displayUnit = DisplayUnitFor(cfg);

                if (_channelByCode.TryGetValue(key, out var existing))
                {
                    existing.Name = displayName;
                    existing.Unit = displayUnit;
                }
                else
                {
                    var ch = new Controls.TrendChannel
                    {
                        Name = displayName,
                        Unit = displayUnit,
                        Color = _palette[paletteIdx % _palette.Length],
                    };
                    _channelByCode[key] = ch;
                    Channels.Add(ch);
                    // 同一个实例按分组加入对应图表集合（压力/温度/流量），使曲线更新自动同步
                    GroupCollectionFor(cfg.CurveChannel).Add(ch);
                }
                paletteIdx++;
            }
        }

        // 移除未选中装置/已删除变量对应的通道
        foreach (var key in _channelByCode.Keys.ToList())
        {
            if (wantedKeys.Contains(key)) continue;
            var ch = _channelByCode[key];
            Channels.Remove(ch);
            // 从三个分组集合中移除（不在其中则为空操作）
            PressureChannels.Remove(ch);
            TempChannels.Remove(ch);
            FlowChannels.Remove(ch);
            _channelByCode.Remove(key);
        }

        // 关联每个变量行到它的趋势通道（供表格显示颜色、勾选控制显隐；单/多装置通用）
        foreach (var kvp in _monitorVarByKey)
        {
            kvp.Value.Channel = _channelByCode.TryGetValue(kvp.Key, out var ch) ? ch : null;
        }
    }

    public string BoundaryNote => "支持 Modbus TCP 和西门子 S7 协议读取 PLC 实时变量；不在本软件中下发试验任务或执行现场控制。";

    // ========== 命令 ==========

    /// <summary>保存配置</summary>
    public ICommand SaveConfigCommand => new RelayCommand(SaveConfig, () => PermissionGuard.Can(Perms.RealtimeEdit));
    /// <summary>添加变量</summary>
    public ICommand AddVariableCommand => new RelayCommand(AddVariable, () => PermissionGuard.Can(Perms.RealtimeEdit));
    /// <summary>删除选中变量</summary>
    public ICommand RemoveVariableCommand => new RelayCommand(() => RemoveVariable(SelectedMonitorVariable), () => PermissionGuard.Can(Perms.RealtimeDelete));
    /// <summary>保存 PLC 地址</summary>
    public ICommand SavePlcIpCommand => new RelayCommand(SavePlcIp, () => PermissionGuard.Can(Perms.RealtimeEdit));

    [ObservableProperty]
    private MonitorVariable? _selectedMonitorVariable;

    /// <summary>
    /// 连接 PLC：连接当前所选装置（自动识别 Modbus / 西门子 S7 协议）。
    /// 失败只影响该装置（可按其配置仿真降级）。
    /// </summary>
    [RelayCommand]
    private async Task ConnectPlcAsync()
    {
        if (IsConnected || _isSwitchingDevice) return;

        try
        {
            var targets = SelectedDevices();
            if (targets.Count == 0)
            {
                ConnectionState = "没有可连接的装置（plc-registers.json 未配置）";
                return;
            }

            await Task.WhenAll(targets.Select(ConnectDeviceAsync));
            UpdateConnectionSummary();
        }
        catch (Exception ex)
        {
            ConnectionState = $"连接异常：{ex.Message}";
            // 记录完整异常（含堆栈、内部异常）到 logs 日志
            Log.Error(ex, "[实时监视] 连接 PLC 异常");
        }
    }

    /// <summary>当前采集的装置（独占模型：同一时刻只有所选的一台；未选择时回退第一台）。</summary>
    private List<DeviceRuntime> SelectedDevices()
    {
        var selectedCode = SelectedPlcDevice?.DeviceCode;
        if (string.IsNullOrEmpty(selectedCode) && _devices.Count > 0)
        {
            selectedCode = _devices[0].DeviceCode;
        }

        return _devices.Where(d => string.Equals(d.DeviceCode, selectedCode, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private PlcDeviceItem? _selectedPlcDevice;

    // 装置切换自动续采进行中标志：防止切换期间的二次切换/手动连接并发
    private bool _isSwitchingDevice;

    /// <summary>
    /// 当前查看/采集的装置（多装置模式单选）。
    /// 监视中切换时自动续采：结束当前记录（完整落盘）→ 切换 → 连接新装置 → 相同对象开新记录，
    /// 见 <see cref="SwitchDeviceAndContinueAsync"/>。切换进行中忽略再次切换并回弹。
    /// </summary>
    public PlcDeviceItem? SelectedPlcDevice
    {
        get => _selectedPlcDevice;
        set
        {
            if (_disposed) return;

            if (_isSwitchingDevice && !ReferenceEquals(value, _selectedPlcDevice))
            {
                // 切换流程进行中：忽略新选择并回弹，等当前切换完成后再切
                OnPropertyChanged(nameof(SelectedPlcDevice));
                return;
            }

            if (IsMonitoring && !ReferenceEquals(value, _selectedPlcDevice))
            {
                // 监视中切换：先接受选择（断开旧连接、重建通道——此间 tick 因无活跃装置自然空转，
                // 已采数据仍在内存缓冲中，由随后的停止步骤完整落盘），再异步编排续采。
                if (!SetProperty(ref _selectedPlcDevice, value)) return;
                OnSelectedPlcDeviceChanged();
                _ = SwitchDeviceAndContinueAsync();
                return;
            }

            if (!SetProperty(ref _selectedPlcDevice, value)) return;
            OnSelectedPlcDeviceChanged();
        }
    }

    /// <summary>
    /// 监视中切换装置后的自动续采：停止当前监视（与手动停止同路径，完整落盘当前记录）→
    /// 连接新装置 → 用相同的项目/机组/试验对象自动开始新记录。
    /// 任一步失败则停在"已停止"状态——原记录已安全保存，用户可手动连接/开始或切回。
    /// </summary>
    private async Task SwitchDeviceAndContinueAsync()
    {
        _isSwitchingDevice = true;
        var previousRecord = _currentRecordCode;
        var targetCode = SelectedPlcDevice?.DeviceCode ?? "?";
        try
        {
            Log.Information("[实时监视] 监视中切换装置 {From} → {To}，自动续采", previousRecord is null ? "-" : "记录 " + previousRecord, targetCode);

            ConnectionState = "已切换装置，正在结束当前记录…";
            await StopMonitoringAsync();

            ConnectionState = $"正在连接装置 {targetCode}…";
            foreach (var d in SelectedDevices())
            {
                await ConnectDeviceAsync(d);   // 失败走 HandleDeviceConnectionFailureAsync（含按配置的仿真降级）
            }
            UpdateConnectionSummary();

            if (!IsConnected)
            {
                ConnectionState = $"装置 {targetCode} 连接失败。原记录 {previousRecord} 已保存，请手动连接后再开始监视。";
                SessionInfo = $"记录已保存：{previousRecord}";
                return;
            }

            await StartMonitoringAsync();     // 相同对象自动开新记录；校验不通过会弹窗说明原因
            if (IsMonitoring)
            {
                Log.Information("[实时监视] 切换续采完成：{Old} 已保存，新记录 {New}（装置 {Device}）",
                    previousRecord, _currentRecordCode, targetCode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] 切换装置自动续采异常：{Error}", ex.Message);
            ConnectionState = $"切换装置异常：{ex.Message}（原记录 {previousRecord} 已保存）";
        }
        finally
        {
            _isSwitchingDevice = false;
        }
    }

    /// <summary>切换装置：断开旧连接、重建通道（三张图切换为新装置的曲线）、同步地址框。</summary>
    private void OnSelectedPlcDeviceChanged()
    {
        // 已连接状态下切换：断开全部旧连接，提示用户连接新装置
        if (IsConnected)
        {
            try
            {
                foreach (var d in _devices)
                {
                    try { d.Connection?.DisconnectAsync(); } catch { /* 忽略 */ }
                    try { (d.Connection as IDisposable)?.Dispose(); } catch { /* 忽略 */ }
                    d.Connection = null;
                    d.IsAlive = true;
                }
                IsConnected = false;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[实时监视] 切换装置时断开旧连接发生警告");
            }
        }

        SyncChannelsFromDevices();
        if (PrimaryDevice is { } primary)
        {
            PlcIpAddress = primary.ConnectionConfig.IpAddress;
        }
        ConnectionState = SelectedPlcDevice == null ? "未连接" : "已切换装置，请点击\"连接 PLC\"";
    }

    /// <summary>装置选项状态变化：未监视时汇总连接状态。</summary>
    private void OnPlcDeviceItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(PlcDeviceItem.Status))
        {
            if (!IsMonitoring) UpdateConnectionSummary();
        }
    }

    /// <summary>连接单台装置：按其配置选择协议，连接成功后试读验证可读性。</summary>
    private async Task ConnectDeviceAsync(DeviceRuntime d)
    {
        var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == d.DeviceCode);
        var plcType = (d.ConnectionConfig.PlcType ?? "Modbus").ToUpper();
        var protocol = d.ConnectionConfig.Protocol ?? "tcp";
        var ip = d.ConnectionConfig.IpAddress;
        var port = d.ConnectionConfig.Port > 0 ? d.ConnectionConfig.Port : (plcType == "SIEMENSS7" ? 102 : 502);

        DeviceResult result;
        bool realUsable = false;

        if (plcType == "SIEMENSS7")
        {
            // ========== 西门子 S7 协议 ==========
            Log.Information("[实时监视] {Device} 使用西门子 S7 协议连接，IP={IP}, Port={Port}, CPU={Protocol}", d.DeviceCode, ip, port, protocol);

            var s7Plc = new SiemensS7PlcConnection(
                cpuType: protocol,
                rack: d.ConnectionConfig.Rack,
                slot: d.ConnectionConfig.Slot);
            result = await s7Plc.ConnectAsync(ip, port);

            if (result.IsSuccess)
            {
                realUsable = await ProbeS7ReadableAsync(s7Plc, d.RegisterConfigs);
            }

            if (result.IsSuccess && realUsable)
            {
                d.Connection = s7Plc;
                d.IsAlive = true;
                if (item != null) item.Status = $"已连接 {ip}:{port}";
                Log.Information("[实时监视] {Device} 已连接西门子 PLC {IP}:{Port} ({Protocol})", d.DeviceCode, ip, port, protocol);
            }
            else
            {
                var reason = result.IsSuccess
                    ? $"连接成功但读不到有效数据（IP={ip}:{port}, CPU={protocol}, Rack={d.ConnectionConfig.Rack}, Slot={d.ConnectionConfig.Slot}）；请检查西门子变量地址配置及 DB 块是否可读"
                    : result.Error;
                try { s7Plc.Dispose(); } catch (Exception dex) { Log.Debug(dex, "[实时监视] 释放 S7 连接失败"); }
                await HandleDeviceConnectionFailureAsync(d, "西门子 S7", reason);
            }
        }
        else
        {
            // ========== Modbus 协议（默认） ==========
            Log.Information("[实时监视] {Device} 使用 Modbus 协议连接，IP={IP}, Port={Port}", d.DeviceCode, ip, port);

            var realPlc = new ModbusPlcConnection(protocol);
            result = await realPlc.ConnectAsync(ip, port);

            // 真实连接成功后，试读一次验证能否拿到有效数据
            if (result.IsSuccess)
            {
                realUsable = await ProbeRealReadableAsync(realPlc, d.RegisterConfigs);
            }

            if (result.IsSuccess && realUsable)
            {
                d.Connection = realPlc;
                d.IsAlive = true;
                if (item != null) item.Status = $"已连接 {protocol}://{ip}:{port}";
                Log.Information("[实时监视] {Device} 已连接 PLC {Protocol}://{IP}:{Port}", d.DeviceCode, protocol, ip, port);
            }
            else
            {
                var reason = result.IsSuccess
                    ? $"连接成功但读不到有效数据（{protocol}://{ip}:{port}）；TCP 可达但可能无 Modbus 服务，或寄存器地址配置有误"
                    : result.Error;
                try { realPlc.Dispose(); } catch (Exception dex) { Log.Debug(dex, "[实时监视] 释放 Modbus 连接失败"); }
                await HandleDeviceConnectionFailureAsync(d, "Modbus", reason);
            }
        }
    }

    /// <summary>
    /// 自动重连单台装置（用于 TickAsync 连续失败后的恢复）。
    /// 复用该装置已有配置（IP、端口、协议），不创建新的试验记录。
    /// 返回 true 表示重连成功，false 表示失败。
    /// </summary>
    private async Task<bool> TryReconnectDeviceAsync(DeviceRuntime d)
    {
        try
        {
            Log.Information("[实时监视] {Device} 开始自动重连...", d.DeviceCode);

            // 1. 释放旧的连接
            if (d.Connection != null)
            {
                try { d.Connection.Dispose(); } catch { /* 忽略释放异常 */ }
                d.Connection = null;
            }

            // 2. 根据原配置创建新连接
            var plcType = (d.ConnectionConfig.PlcType ?? "Modbus").ToUpper();
            var protocol = d.ConnectionConfig.Protocol ?? "tcp";
            var ip = d.ConnectionConfig.IpAddress;
            var port = d.ConnectionConfig.Port > 0 ? d.ConnectionConfig.Port : (plcType == "SIEMENSS7" ? 102 : 502);

            if (plcType == "SIEMENSS7")
            {
                var s7Plc = new SiemensS7PlcConnection(
                    cpuType: protocol,
                    rack: d.ConnectionConfig.Rack,
                    slot: d.ConnectionConfig.Slot);
                var result = await s7Plc.ConnectAsync(ip, port);
                if (result.IsSuccess)
                {
                    d.Connection = s7Plc;
                }
                else
                {
                    try { s7Plc.Dispose(); } catch { }
                    return false;
                }
            }
            else
            {
                var modbusPlc = new ModbusPlcConnection(protocol);
                var result = await modbusPlc.ConnectAsync(ip, port);
                if (result.IsSuccess)
                {
                    d.Connection = modbusPlc;
                }
                else
                {
                    try { modbusPlc.Dispose(); } catch { }
                    return false;
                }
            }

            Log.Information("[实时监视] {Device} 自动重连成功: {IP}:{Port}", d.DeviceCode, ip, port);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] {Device} 自动重连异常", d.DeviceCode);
            return false;
        }
    }

    /// <summary>
    /// 统一处理单台装置的连接失败：
    /// 1) 始终把失败原因（含配置详情）以 Error 级别写入 logs 日志，方便排查现场连接问题；
    /// 2) 默认不静默降级为仿真数据（AllowSimulationFallback=false）；仅当该装置显式开启
    ///    仿真降级时才连接模拟 PLC（降级只作用于该装置，不影响其余装置）。
    /// </summary>
    private async Task HandleDeviceConnectionFailureAsync(DeviceRuntime d, string protocolLabel, string reason)
    {
        Log.Error("[实时监视] {Device} {Protocol} PLC 连接失败：{Reason}", d.DeviceCode, protocolLabel, reason);
        var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == d.DeviceCode);

        if (!d.ConnectionConfig.AllowSimulationFallback)
        {
            // 不降级：该装置保持未连接，原因显示到装置状态（整体状态由 UpdateConnectionSummary 汇总）
            d.Connection = null;
            if (item != null) item.Status = $"失败：{reason}";
            return;
        }

        // 该装置显式开启了仿真降级（演示/无 PLC 环境）
        Log.Warning("[实时监视] {Device} 已启用仿真降级（AllowSimulationFallback=true），改用模拟 PLC 数据", d.DeviceCode);
        var mock = new MockPlcConnection();
        var mockResult = await mock.ConnectAsync("127.0.0.1", 502);
        if (mockResult.IsSuccess)
        {
            d.Connection = mock;
            d.IsAlive = true;
            _usedSimulationFallback = true; // 会话内任一装置降级即标记，落库时在记录备注中标注
            if (item != null) item.Status = "[模拟] 已连接（仿真数据演示模式）";
            Log.Information("[实时监视] {Device} 已连接模拟 PLC", d.DeviceCode);
        }
        else
        {
            d.Connection = null;
            if (item != null) item.Status = $"失败：{reason}";
        }
    }

    /// <summary>汇总装置连接状态到 IsConnected / ConnectionState。</summary>
    private void UpdateConnectionSummary()
    {
        // 独占模型：只连接当前所选装置，状态直接反映该装置
        var item = SelectedPlcDevice;
        var runtime = item != null ? _deviceByCode.GetValueOrDefault(item.DeviceCode) : null;
        IsConnected = runtime?.Connection != null;

        if (runtime == null)
        {
            ConnectionState = "未连接";
        }
        else if (runtime.Connection != null)
        {
            ConnectionState = $"已连接 {runtime.ConnectionConfig.IpAddress}:{runtime.ConnectionConfig.Port}";
        }
        else if (item != null && !string.IsNullOrEmpty(item.Status))
        {
            ConnectionState = item.Status.StartsWith("失败") || item.Status.StartsWith("连接失败")
                ? $"{item.Status}（详情见 logs 日志）"
                : item.Status;
        }
        else
        {
            ConnectionState = "未连接";
        }
    }

    /// <summary>
    /// 试读一次寄存器，判断真实连接是否能拿到有效数据。
    /// 全部为 NaN（读取失败）则视为不可用（只是 TCP 通但无 Modbus 服务/PLC）。
    /// </summary>
    private async Task<bool> ProbeRealReadableAsync(IModbusPlcConnection plc, List<PlcVariableConfig> configs)
    {
        try
        {
            var requests = configs
                .Select(vc => new PlcRegisterRequest { Address = vc.RegisterAddress, DataType = vc.DataType })
                .ToList();
            if (requests.Count == 0) return false;

            var probe = await plc.ReadMultipleAsync(requests);
            if (!probe.IsSuccess || probe.Data == null || probe.Data.Count == 0)
            {
                Log.Warning("[实时监视] Modbus 试读失败：{Error}", probe.Error ?? "无返回数据");
                return false;
            }

            // 只要有任一通道读到有效数值，就视为真实 PLC 可用（与 S7 探测逻辑一致）。
            // 避免个别地址配错就把整台真实 PLC 误判为不可用、静默降级到模拟数据。
            bool ok = probe.Data.Values.Any(v => !double.IsNaN(v) && !double.IsInfinity(v));
            if (!ok)
                Log.Warning("[实时监视] Modbus 试读返回 {Count} 个寄存器但全部无效（NaN/Inf），请检查寄存器地址配置", probe.Data.Count);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] Modbus 试读异常");
            return false;
        }
    }

    /// <summary>
    /// 试读一次西门子 PLC 变量，判断真实连接是否能拿到有效数据。
    /// </summary>
    private async Task<bool> ProbeS7ReadableAsync(SiemensS7PlcConnection plc, List<PlcVariableConfig> configs)
    {
        try
        {
            // 构建西门子地址读取请求
            var requests = configs
                .Where(vc => !string.IsNullOrEmpty(vc.SiemensAddress))
                .Select(vc => new SiemensReadRequest { SiemensAddress = vc.SiemensAddress, DataType = vc.DataType })
                .ToList();

            if (requests.Count == 0)
            {
                // 如果没有配置西门子地址，尝试用第一个变量的寄存器地址兼容读取
                var firstConfig = configs.FirstOrDefault();
                if (firstConfig == null)
                {
                    Log.Warning("[实时监视] S7 试读跳过：未配置任何变量地址");
                    return false;
                }

                var probe = await plc.ReadDoubleAsync(firstConfig.RegisterAddress);
                if (!probe.IsSuccess)
                    Log.Warning("[实时监视] S7 兼容试读失败：{Error}", probe.Error);
                return probe.IsSuccess && !double.IsNaN(probe.Data) && !double.IsInfinity(probe.Data);
            }

            var probeMulti = await plc.ReadMultipleBySiemensAddressAsync(requests);
            if (!probeMulti.IsSuccess || probeMulti.Data == null || probeMulti.Data.Count == 0)
            {
                Log.Warning("[实时监视] S7 试读失败：{Error}", probeMulti.Error ?? "无返回数据");
                return false;
            }

            // 只要有任一通道读取成功就视为可用
            bool ok = probeMulti.Data.Values.Any(v => !double.IsNaN(v) && !double.IsInfinity(v));
            if (!ok)
                Log.Warning("[实时监视] S7 试读返回 {Count} 个变量但全部无效（NaN/Inf），请检查西门子地址（DB块/偏移/类型）配置", probeMulti.Data.Count);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] S7 试读异常");
            return false;
        }
    }

    /// <summary>
    /// 断开全部 PLC 装置
    /// </summary>
    [RelayCommand]
    private async Task DisconnectPlcAsync()
    {
        if (IsMonitoring)
        {
            await StopMonitoringAsync();
        }

        foreach (var d in _devices)
        {
            try
            {
                if (d.Connection != null)
                {
                    await d.Connection.DisconnectAsync();
                    d.Connection = null;
                    d.IsAlive = true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[实时监视] 断开装置 {Device} 连接时发生警告", d.DeviceCode);
            }
            var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == d.DeviceCode);
            if (item != null) item.Status = "未连接";
        }

        IsConnected = false;
        ConnectionState = "未连接";
    }

    /// <summary>
    /// 开始监视
    /// </summary>
    [RelayCommand]
    private async Task StartMonitoringAsync()
    {
        Log.Information("[实时监视] ========== 开始监视请求 ==========");
        Log.Information("[实时监视] 连接状态：{IsConnected}, 监视状态：{IsMonitoring}", IsConnected, IsMonitoring);

        if (!IsConnected || IsMonitoring) return;

        // 新监视会话：重置仿真降级标记（上一会话是否降级不影响本会话的记录标注）
        _usedSimulationFallback = false;

        // 验证试验对象选择
        if (SelectedProject == null || SelectedUnit == null || SelectedObject == null)
        {
            Log.Warning("[实时监视] 试验对象选择 incomplete: Project={Project}, Unit={Unit}, Object={Object}",
                SelectedProject?.Code ?? "null",
                SelectedUnit?.Code ?? "null",
                SelectedObject?.Code ?? "null");

            ConnectionState = "请先选择项目、机组和试验对象";
            MessageBox.Show("请先在顶部选择项目、机组和试验对象，然后再开始监视。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Log.Information("[实时监视] 试验对象：Project={Project}, Unit={Unit}, Object={Object}",
            SelectedProject.Code, SelectedUnit.Code, SelectedObject.Code);

        try
        {
            // 校验测量装置选择：记录归属装置必须是台账中真实存在的装置。
            // 不能写死编号：若该编号不在 MeasurementDevices 台账里，
            // TestRecord 会触发外键 FK_TestRecords_MeasurementDevices_DeviceCode 失败，
            // 连带同批插入的 TestProcessData 也一起回滚（曾表现为两条外键冲突）。
            //
            // 记录归属规则：单装置模式=台账下拉所选装置（原有行为）；
            // 多装置模式=当前所选的 PLC 装置（主装置），其 DeviceCode 必须在台账登记。
            string recordDeviceCode;
            if (_multiDevice)
            {
                var selectedPlc = SelectedDevices();
                if (selectedPlc.Count == 0)
                {
                    ConnectionState = "请先选择 PLC 装置";
                    MessageBox.Show("请先在顶部选择一台 PLC 装置，然后再开始监视。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var primaryCode = selectedPlc[0].DeviceCode;
                var ledgerHit = AvailableDevices.FirstOrDefault(d =>
                    d.DeviceCode.Equals(primaryCode, StringComparison.OrdinalIgnoreCase));
                if (ledgerHit == null)
                {
                    ConnectionState = $"当前装置 {primaryCode} 未在台账登记";
                    MessageBox.Show(
                        $"当前所选装置\"{primaryCode}\"未在测量装置台账中登记，\n" +
                        "试验记录需要归属到台账中的真实装置。\n" +
                        "请将 plc-registers.json 中该装置的 DeviceCode 改为台账中已登记的装置编号，或在台账中登记该装置。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                recordDeviceCode = ledgerHit.DeviceCode;
            }
            else
            {
                if (SelectedDevice == null)
                {
                    Log.Warning("[实时监视] 未选择测量装置");
                    ConnectionState = "请先选择测量装置";
                    var hint = AvailableDevices.Count == 0
                        ? "测量装置台账中没有任何装置，无法开始监视。\n请先在\"测量装置台账\"中登记至少一台装置后再试。"
                        : "请先在顶部选择测量装置，然后再开始监视。";
                    MessageBox.Show(hint, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                recordDeviceCode = SelectedDevice.DeviceCode;
            }

            // 创建试验记录
            using var context = DbContextFactory.CreateDbContext();
            var recordCode = $"{SelectedProject.Code}_{SelectedUnit.Code}_{SelectedObject.Code}_{DateTime.Now:yyyyMMddHHmmssfff}";

            Log.Information("[实时监视] 生成记录编码：{RecordCode}", recordCode);

            var testRecord = new TestRecord
            {
                RecordCode = recordCode,
                ProjectCode = SelectedProject.Code,
                UnitCode = SelectedUnit.Code,
                ObjectCode = SelectedObject.Code,
                ObjectName = SelectedObject.Name,
                ObjectType = SelectedObject.NodeType,
                DeviceCode = recordDeviceCode, // 记录归属装置（单装置=台账下拉；多装置=主装置）
                TestTime = DateTime.Now,
                ImportTime = DateTime.Now,
                Operator = Services.Security.UserSession.Current?.User.UserName ?? "system",
                Result = TestResult.Unknown,
                CreatedAt = DateTime.Now,
            };

            Log.Information("[实时监视] 创建 TestRecord：Code={Code}, Project={Project}, Unit={Unit}, Object={Object}, Device={Device}",
                testRecord.RecordCode,
                testRecord.ProjectCode,
                testRecord.UnitCode,
                testRecord.ObjectCode,
                testRecord.DeviceCode);

            // 绑定试验对象的默认配方（与数据上传路径 DataUploadService.ValidateAndUploadAsync 的策略一致）：
            // 有默认配方 → 写 TestRecipeId + 固化配方快照 + 版本号。PersistSnapshot 判定时
            // "配方限值优先于节点限值"，两条数据入口的判定口径由此保持一致；
            // 此前实时记录从不绑定配方，同一对象经导入/实时两条路径会得到不同判定依据。
            if (SelectedObject.DefaultRecipeId.HasValue)
            {
                var recipeId = SelectedObject.DefaultRecipeId.Value;
                testRecord.TestRecipeId = recipeId;
                testRecord.RecipeSnapshotJson = await AppServices.RecipeService.CreateSnapshotForTestAsync(recipeId);
                testRecord.RecipeVersionNumber = await AppServices.RecipeService.GetCurrentVersionAsync(recipeId);
                Log.Information("[实时监视] 已绑定默认配方：RecipeId={RecipeId}, Version={Version}",
                    recipeId, testRecord.RecipeVersionNumber);
            }

            context.TestRecords.Add(testRecord);

            // 创建过程数据占位
            var processData = new TestProcessData
            {
                RecordCode = recordCode,
                CreatedAt = DateTime.Now,
            };

            Log.Information("[实时监视] 创建 TestProcessData：RecordCode={Code}", recordCode);

            context.TestProcessData.Add(processData);

            Log.Information("[实时监视] 正在保存数据库...");
            await context.SaveChangesAsync();
            Log.Information("[实时监视] 数据库保存成功");

            // 初始化实时数据服务
            _realtimeDataService = AppServices.RealtimeDataService;

            // 变量配置服务（保存配置用）。配置本体已在构造时从数据库加载；
            // 此处不再重复加载，避免把表格中未保存的修改静默回滚（见构造函数注释）。
            _variableConfigService = AppServices.MonitorVariableConfigService;
            var session = await _realtimeDataService.CreateSessionAsync(
                projectCode: SelectedProject.Code,
                unitCode: SelectedUnit.Code,
                objectCode: SelectedObject.Code,
                sampleIntervalMs: SampleIntervalMs);

            _currentSessionCode = session.SessionCode;
            _currentRecordCode = recordCode;

            Log.Information("[实时监视] 会话创建成功：Session={Session}, Record={Record}",
                session.SessionCode, recordCode);

            SessionInfo = $"记录：{recordCode}";

            // 定格试验对象选择：监视期间三个下拉锁定（见 GuardMonitoringSelection）
            _monitoringLockProject = SelectedProject;
            _monitoringLockUnit = SelectedUnit;
            _monitoringLockObject = SelectedObject;

            IsMonitoring = true;
            _tickCount = 0;
            _readCts = new CancellationTokenSource();

            // 重置各装置的读取失败状态（IsAlive 跟随连接是否存在）
            foreach (var d in _devices)
            {
                d.ConsecutiveReadFailures = 0;
                d.IsAlive = d.Connection != null;
            }

            // 清空曲线和采样时间
            PressurePoints.Clear();
            FlowPoints.Clear();
            TempPoints.Clear();
            Flow2Points.Clear();
            Pressure2Points.Clear();
            TimeAxisPoints.Clear();
            // 清空所有动态通道曲线
            foreach (var ch in Channels) ch.Points.Clear();
            _sampleSeq = 0;
            _monitorStartTime = DateTime.Now;  // 记录监视开始时间，用于计算相对秒数

            // 清空全量历史缓冲，开始新一段采集
            _fullSampleTimes.Clear();
            _fullChannelData.Clear();
            _lastAutoSaveSeq = 0;

            // 启动定时器
            _timer.Start();

            Log.Information("[实时监视] ========== 监视已启动 ==========");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] 启动监视失败：{Error}", ex.Message);
            Log.Error(ex, "[实时监视] 异常详情：{StackTrace}", ex.StackTrace);
            ConnectionState = $"启动失败：{ex.Message}";
            IsMonitoring = false;
        }
    }

    /// <summary>当前试验记录编码</summary>
    private string? _currentRecordCode;

    /// <summary>
    /// 停止监视
    /// </summary>
    [RelayCommand]
    private async Task StopMonitoringAsync()
    {
        Log.Information("[实时监视] ========== 停止监视请求 ==========");

        if (!IsMonitoring) return;

        IsMonitoring = false;
        _timer.Stop();
        _readCts?.Cancel();

        Log.Information("[实时监视] 定时器已停止，全量采样点数：{Count}", _fullSampleTimes.Count);

        // 保存最终的全量曲线数据（等待任何进行中的自动保存完成后再存最终版）
        if (_currentRecordCode != null)
        {
            await PersistProcessDataAsync(waitForTurn: true);
            SessionInfo = $"记录已保存：{_currentRecordCode}";
            Log.Information("[实时监视] ========== 监视已停止，数据已保存 ==========");
        }
        else
        {
            Log.Warning("[实时监视] 没有当前记录编码，跳过保存");
        }
    }

    /// <summary>
    /// 把当前累积的【全量】数据写入 TestProcessData / TestRecord。
    /// 供“停止监视”、周期自动保存、Dispose 兜底共用。
    /// waitForTurn=false：若已有保存在进行则跳过（自动保存用）；true：等其完成再存（停止用）。
    /// 快照在 UI 线程完成，避免与 tick 追加并发。
    /// </summary>
    private async Task PersistProcessDataAsync(bool waitForTurn = false)
    {
        var recordCode = _currentRecordCode;
        if (string.IsNullOrEmpty(recordCode)) return;

        if (waitForTurn) await _persistLock.WaitAsync();
        else if (!await _persistLock.WaitAsync(0)) return;

        try
        {
            // 快照在 UI 线程做（与 tick 追加互斥），但用 InvokeAsync 异步等待：
            // 同步 Invoke 会让定时器线程阻塞等 UI 线程（周期性卡顿 + UI 忙时的死锁风险）。
            var shot = _uiDispatcher.CheckAccess()
                ? SnapshotFull()
                : await _uiDispatcher.InvokeAsync(SnapshotFull);

            if (shot.Times.Length == 0 && shot.Channels.Count == 0) return;

            await Task.Run(() => PersistSnapshot(recordCode!, shot.Times, shot.Channels));
            Log.Information("[实时监视] 已保存全量数据：{N} 采样点, {C} 通道, Record={Code}",
                shot.Times.Length, shot.Channels.Count, recordCode);

            // 只有持久化成功后才允许裁剪内存缓冲：把"已落库"作为裁剪前置条件。
            // 原先调用点 fire-and-forget 发起保存后立即裁剪，保存失败/被跳过（抢锁失败、DB 闪断）
            // 时被裁数据从未写入数据库且无法找回。裁剪须回 UI 线程执行（与 tick 追加互斥）。
            if (_fullSampleTimes.Count > MaxBufferPoints)
            {
                if (_uiDispatcher.CheckAccess()) TrimMemoryBuffer();
                else _ = _uiDispatcher.BeginInvoke(TrimMemoryBuffer); // 异步投递不等待（保存可能在后台线程完成）
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] 保存全量数据失败：{Error}", ex.Message);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>
    /// 清理内存缓冲区：保留最近 MaxBufferPoints 个数据点，删除已保存的旧数据。
    /// 同时裁剪 _fullSampleTimes、_fullChannelData、动态通道 ch.Points、
    /// 旧格式固定通道集合以及共享时间轴 TimeAxisPoints（统一按同一 trimCount，保证索引对齐）。
    /// 必须在 UI 线程调用（与 tick 追加互斥）。
    /// </summary>
    private void TrimMemoryBuffer()
    {
        try
        {
            if (_fullSampleTimes.Count <= MaxBufferPoints) return;

            // 计算需要删除的点数
            int trimCount = _fullSampleTimes.Count - MaxBufferPoints;

            // 1. 裁剪采样时间
            _fullSampleTimes.RemoveRange(0, trimCount);

            // 2. 统一按 trimCount 裁剪所有通道（确保时间/数据对齐）
            foreach (var kvp in _fullChannelData)
            {
                if (kvp.Value.Count >= trimCount)
                {
                    kvp.Value.RemoveRange(0, trimCount);
                }
                else
                {
                    // 通道数据比裁剪量还少，全清
                    kvp.Value.Clear();
                }
            }

            // 3. 裁剪动态通道曲线数据（ch.Points）- 修复 P0 遗漏
            foreach (var kvp in _channelByCode)
            {
                var ch = kvp.Value;
                if (ch.Points.Count >= trimCount)
                {
                    // BulkObservableCollection 不支持 RemoveRange，用 ReplaceAll 裁剪
                    var remaining = ch.Points.Skip(trimCount).ToList();
                    ch.Points.ReplaceAll(remaining);
                }
                else
                {
                    ch.Points.Clear();
                }
            }

            // 4. 裁剪旧格式固定通道集合（PressurePoints 等）。这些集合仅供 SaveCurveAsync 周期
            // checkpoint 序列化、无 UI 绑定，之前从不裁剪 → 长会话下无界累积且每次全量重序列化（O(n²)）。
            // 各自独立裁到 MaxBufferPoints 以内，使内存有界、单次 checkpoint 序列化成本恒定。
            TrimLegacyChannel(PressurePoints);
            TrimLegacyChannel(FlowPoints);
            TrimLegacyChannel(TempPoints);
            TrimLegacyChannel(Flow2Points);
            TrimLegacyChannel(Pressure2Points);

            // 5. 裁剪共享时间轴集合（TrendChart.TimePoints 的数据源）。
            // 必须与 ch.Points 用同一 trimCount：时间轴 ReplaceAll 触发 Reset 全量重建时，
            // series 的 X 取 _timeValues[i]、Y 取 ch.Points[i]，两边索引不同步会整体错位平移。
            // 之前 TimeAxisPoints 从不裁剪 → 无界增长，且裁剪通道后与时间轴索引错位。
            // 放在最后：各通道先裁完，时间轴 Reset 触发 ResyncAll 时读到的已是最终对齐状态。
            TimeAxisPoints.ReplaceAll(TimeAxisPoints.Skip(trimCount).ToList());

            Log.Information("[实时监视] 内存缓冲区已清理：删除 {TrimCount} 个旧数据点，保留 {Count} 个，已裁剪 {Channels} 个动态通道",
                trimCount, _fullSampleTimes.Count, _channelByCode.Count);
        }
        catch (Exception ex)
        {
            Log.Warning("[实时监视] 清理内存缓冲区失败：{Error}", ex.Message);
        }
    }

    /// <summary>
    /// 把旧格式固定通道集合裁剪到 MaxBufferPoints 以内（保留最近的点，删除最旧的）。
    /// 必须在 UI 线程调用（与 tick 追加互斥）。
    /// </summary>
    private static void TrimLegacyChannel(BulkObservableCollection<double> channel)
    {
        int over = channel.Count - MaxBufferPoints;
        if (over <= 0) return;
        channel.ReplaceAll(channel.Skip(over).ToList());
    }

    /// <summary>快照全量缓冲（必须在 UI 线程调用，与 tick 追加互斥）。</summary>
    private (DateTime[] Times, Dictionary<string, (string Name, string Unit, double[] Data)> Channels) SnapshotFull()
    {
        var times = _fullSampleTimes.ToArray();
        var snap = new Dictionary<string, (string Name, string Unit, double[] Data)>();
        foreach (var (code, list) in _fullChannelData)
        {
            var ch = _channelByCode.TryGetValue(code, out var c) ? c : null;
            snap[code] = (ch?.Name ?? code, ch?.Unit ?? string.Empty, list.ToArray());
        }
        return (times, snap);
    }

    /// <summary>同步把一份数据快照写入库（在后台线程或 Dispose 中调用）。</summary>
    private void PersistSnapshot(string recordCode, DateTime[] times,
        Dictionary<string, (string Name, string Unit, double[] Data)> snap)
    {
        double[] timeAxis = times.Length > 0
            ? times.Select(t => (t - times[0]).TotalSeconds).ToArray()
            : [];

        var channelsDict = new Dictionary<string, ChannelData>();
        foreach (var (code, s) in snap)
        {
            // 数值与单位标签保持一致：压力通道单位为 kPa，数据 ×1000 后入库
            // （旧列 PressureCurveJson 与 TestRecord.TestPressure 仍用下面的原始 MPa 值）
            var data = Helpers.PressureUnitConverter.ScaleToUnit(s.Data, s.Unit);
            channelsDict[code] = new ChannelData
            {
                Name = s.Name, Unit = s.Unit, Data = data,
                Min = data.Length > 0 ? data.Min() : 0,
                Max = data.Length > 0 ? data.Max() : 0,
            };
        }

        // 旧格式固定通道（向后兼容）：按 CurveChannel 从全量数据取对应变量
        // 多装置：扫描全部装置的变量配置，任一命中即取（通道键已带 DeviceCode 前缀）
        double[] FullByCurve(string curve)
        {
            foreach (var dev in _devices)
            {
                var cfg = dev.RegisterConfigs.FirstOrDefault(c =>
                    string.Equals(c.CurveChannel, curve, StringComparison.OrdinalIgnoreCase));
                if (cfg != null && snap.TryGetValue(ChannelKey(dev.DeviceCode, cfg.VariableCode), out var s))
                    return s.Data;
            }
            return [];
        }
        var pressureArray = FullByCurve("Pressure");
        var flowArray = FullByCurve("Flow");
        var flow2Array = FullByCurve("Flow2");
        var tempArray = FullByCurve("Temp");

        using var context = DbContextFactory.CreateDbContext();

        var processData = context.TestProcessData.FirstOrDefault(d => d.RecordCode == recordCode);
        if (processData != null)
        {
            processData.ChannelsJson = System.Text.Json.JsonSerializer.Serialize(channelsDict);
            processData.TimeAxisJson = System.Text.Json.JsonSerializer.Serialize(timeAxis);
            processData.PressureCurveJson = System.Text.Json.JsonSerializer.Serialize(pressureArray);
            processData.FlowCurveJson = System.Text.Json.JsonSerializer.Serialize(flowArray);
            processData.TempCurveJson = System.Text.Json.JsonSerializer.Serialize(tempArray);
            if (pressureArray.Length > 0) { processData.PressureMin = (decimal)pressureArray.Min(); processData.PressureMax = (decimal)pressureArray.Max(); }
            if (flowArray.Length > 0) { processData.FlowMin = (decimal)flowArray.Min(); processData.FlowMax = (decimal)flowArray.Max(); }
            if (tempArray.Length > 0) { processData.TempMin = (decimal)tempArray.Min(); processData.TempMax = (decimal)tempArray.Max(); }
            processData.UpdatedAt = DateTime.Now;
            context.SaveChanges();
        }

        var testRecord = context.TestRecords.FirstOrDefault(r => r.RecordCode == recordCode);
        if (testRecord != null)
        {
            testRecord.ImportTime = DateTime.Now;

            // 仿真降级标记：本会话曾降级到模拟 PLC 时，在备注中显式标注，
            // 防止仿真流量数据被当作真实试验结果（数据库中无其它模拟数据标识）
            if (_usedSimulationFallback)
            {
                const string simTag = "⚠ 数据来自仿真降级（PLC 连接失败），非真实测量";
                testRecord.Remark = string.IsNullOrWhiteSpace(testRecord.Remark)
                    ? simTag
                    : $"{testRecord.Remark} | {simTag}";
            }

            if (pressureArray.Length > 0) testRecord.TestPressure = (decimal)pressureArray.Average();

            // 计算最终泄漏率（取 M1、M2 所有采样点中的最大值）并判定合格/不合格
            if (flowArray.Length > 0 || flow2Array.Length > 0)
            {
                double m1Max = flowArray.Length > 0 ? flowArray.Max() : 0;
                double m2Max = flow2Array.Length > 0 ? flow2Array.Max() : 0;
                testRecord.FinalLeakageRate = (decimal)Math.Max(0, Math.Max(m1Max, m2Max));

                // 获取泄漏限值：优先取关联配方，其次取节点配置
                decimal leakageLimit = 0;
                var node = context.TestObjectPathNodes
                    .AsNoTracking()
                    .FirstOrDefault(n => n.Code == testRecord.ObjectCode);
                if (node?.LeakageLimit.HasValue == true)
                    leakageLimit = node.LeakageLimit.Value;

                if (testRecord.TestRecipeId.HasValue)
                {
                    var recipe = context.TestRecipes
                        .AsNoTracking()
                        .FirstOrDefault(r => r.Id == testRecord.TestRecipeId.Value);
                    if (recipe != null && recipe.LeakageLimit > 0)
                        leakageLimit = recipe.LeakageLimit;
                }

                testRecord.LeakageLimit = leakageLimit;

                // 有系统限值 → 系统判定；没限值 → Unknown
                if (leakageLimit > 0)
                {
                    testRecord.Result = testRecord.FinalLeakageRate <= leakageLimit
                        ? Models.TestResult.Pass
                        : Models.TestResult.Fail;
                }
            }

            context.SaveChanges();
        }
    }

    /// <summary>
    /// 定时器回调：在后台线程并行读取各 PLC 装置，UI 线程批量更新界面。
    /// 架构：并行读取 → 数据准备 → UI 线程批量更新
    /// 多装置：各装置独立连接/独立失败计数；单装置失败只停该装置，全部失败才停止监视。
    /// 注意：各装置共用一条时间轴（每 tick 一点）——装置中途掉线恢复后其通道点数少于
    /// 时间轴点数，X 坐标按各自索引近似对齐（首期简化：统一全局采样间隔）。
    /// </summary>
    private async Task TickAsync()
    {
        // 重入保护：上一次读取尚未完成则跳过本次，避免多个线程池线程并发读写同一 PLC 连接（打乱报文帧）。
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;

        try
        {
            if (_readCts == null) return;

            var cts = _readCts;
            if (cts.IsCancellationRequested) return;

            var activeDevices = SelectedDevices().Where(d => d.Connection != null && d.IsAlive).ToList();
            if (activeDevices.Count == 0) return;

            // ========== 阶段 1：后台线程并行读取所有装置 ==========
            var readResults = new List<(DeviceRuntime Device, Dictionary<string, double>? Data)>();
            await Task.WhenAll(activeDevices.Select(async d =>
            {
                var data = await ReadDeviceAsync(d, cts.Token);
                lock (readResults) readResults.Add((d, data));
            }));

            var successResults = readResults.Where(r => r.Data != null).ToList();

            // 全部装置都失败：不推进时间轴，直接返回（失败处理已在 OnDeviceReadFailureAsync 内完成）
            if (successResults.Count == 0) return;

            // ========== 阶段 2：后台线程准备数据（所有计算、转换都在后台完成）==========
            var uiUpdateList = new List<(string Key, string Name, string StrVal, string Status, double? RawValue, string? CurveChannel)>();
            foreach (var (device, data) in successResults)
            {
                var isSiemensS7 = device.Connection is SiemensS7PlcConnection;
                foreach (var vc in device.RegisterConfigs)
                {
                    var key = ChannelKey(device.DeviceCode, vc.VariableCode);

                    // 根据协议类型选择查找 key
                    string lookupKey = isSiemensS7 ? vc.SiemensAddress : vc.RegisterAddress.ToString();
                    bool hasValue = data!.TryGetValue(lookupKey, out var value) && !double.IsNaN(value) && !double.IsInfinity(value);

                    if (hasValue)
                    {
                        var strVal = (vc.DataType == "ushort" || vc.DataType == "word" || vc.DataType == "int")
                            ? ((ushort)value).ToString()
                            : (vc.DataType == "dword" || vc.DataType == "uint")
                                ? ((uint)value).ToString()
                                : value.ToString("F4");

                        // 压力通道按 kPa 显示（PLC 原始读数 MPa ×1000；存储/缓冲仍为原值）
                        if (Helpers.PressureUnitConverter.IsPressureChannel(vc.CurveChannel))
                        {
                            strVal = (value * 1000.0).ToString("F2");
                        }

                        uiUpdateList.Add((key, vc.VariableName, strVal, "正常", value, vc.CurveChannel));

                        if (_tickCount < 3)
                            Log.Information("[实时监视] {Device} {Addr}({Code}) 值{Value}", device.DeviceCode, lookupKey, vc.VariableCode, strVal);
                    }
                    else
                    {
                        if (_tickCount < 3)
                            Log.Warning("[实时监视] {Device} {Addr} 未返回有效数据", device.DeviceCode, lookupKey);
                        uiUpdateList.Add((key, vc.VariableName, "-", "未读取到数据", null, vc.CurveChannel));
                    }
                }
            }

            var currentTick = _tickCount + 1;

            // ========== 阶段 3：切回 UI 线程批量更新（只在这里更新界面，不做任何 IO）==========
            var sampleTime = DateTime.Now;

            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 全量采样时间（带大小限制，防止内存耗尽）
                    _fullSampleTimes.Add(sampleTime);

                    // 推进 X 轴时间：计算相对监视开始时间的秒数偏移（避免 DateTimeAxis.ToDouble 的大数字）
                    // 必须先于通道数据更新，使图表重绘时 X 轴已是最新窗口。
                    double relativeSeconds = (sampleTime - _monitorStartTime).TotalSeconds;
                    TimeAxisPoints.Add(relativeSeconds);
                    _sampleSeq++;

                    // 批量更新变量列表 + 动态通道曲线/图例
                    foreach (var (key, name, strVal, status, rawValue, curveChannel) in uiUpdateList)
                    {
                        if (_variableItemByCode.TryGetValue(key, out var item))
                        {
                            item.CurrentValue = strVal;
                            item.UpdatedAt = sampleTime.ToString("HH:mm:ss");
                            item.Status = status;
                        }

                        // 变量表格按通道键同步当前值（单/多装置通用）
                        if (_monitorVarByKey.TryGetValue(key, out var mv))
                        {
                            mv.CurrentValue = strVal;
                            mv.UpdatedAt = sampleTime.ToString("HH:mm:ss");
                            mv.Status = status;
                        }

                        // 喂动态通道：每个变量一条曲线，图例显示当前值
                        if (_channelByCode.TryGetValue(key, out var ch))
                        {
                            ch.CurrentValue = strVal;
                            if (rawValue.HasValue)
                            {
                                // 显示曲线：全量保留、不裁剪（左侧滚出屏幕但数据仍在，可拖回查看）
                                // 压力通道按 kPa 显示（×1000）；全量缓冲保持 PLC 原值，入库时按单位换算
                                bool isPressure = Helpers.PressureUnitConverter.IsPressureChannel(curveChannel);
                                ch.Points.Add(isPressure ? rawValue.Value * 1000.0 : rawValue.Value);

                                // 全量缓冲（不裁剪）：按通道键累积该通道所有采样值（原始值）
                                if (!_fullChannelData.TryGetValue(key, out var full))
                                {
                                    full = [];
                                    _fullChannelData[key] = full;
                                }
                                full.Add(rawValue.Value);
                            }
                        }

                        // 更新固定通道数据（用于保存）
                        if (curveChannel != null && rawValue.HasValue)
                        {
                            AddToChannel(curveChannel, rawValue.Value);
                        }
                    }

                    // 契约保护：TrendChart 增量追加依赖"先 TimeAxisPoints.Add（刷新其内部 _timeValues），
                    // 再 ch.Points.Add（按 NewStartingIndex 从 _timeValues 取 X）"的顺序；颠倒会导致曲线 X 值错位。
                    // 时间轴点数必须与采样时间一致；通道点数允许因装置缺数而落后（见 TickAsync 头部注释）。
                    Debug.Assert(TimeAxisPoints.Count == _fullSampleTimes.Count,
                        "TimeAxisPoints 与 _fullSampleTimes 数量错位：追加顺序契约被破坏");

                    // 更新曲线标题状态（每 10 个采样点更新一次计数显示）
                    if (currentTick % 10 == 0)
                    {
                        OnPropertyChanged(nameof(CurveInfoText));
                    }

                    // 周期自动保存：即使用户未点”停止监视”就切走或关闭，也能保住已采集的全量数据。
                    // 自动保存会把整段数据重新序列化写库，数据越长这一步越贵，
                    // 因此保存间隔随已采集点数放大——数据越多存得越稀，单位时间成本保持有界。
                    // （停止/关闭时仍会存最终全量版，间隔放大不影响数据完整性。）
                    int baseTicks = Math.Max(5, 10000 / Math.Max(1, SampleIntervalMs)); // 约 10 秒
                    long n = _fullSampleTimes.Count;
                    int factor = n < 3600 ? 1 : n < 18000 ? 3 : n < 72000 ? 6 : 30;      // 约 10s/30s/60s/5min
                    long autoEvery = (long)baseTicks * factor;
                    if (_sampleSeq - _lastAutoSaveSeq >= autoEvery)
                    {
                        _lastAutoSaveSeq = _sampleSeq;
                        _ = PersistProcessDataAsync();

                        // 内存硬上限保护（3 倍 MaxBufferPoints）：数据库长期不可用、保存一直失败时，
                        // 缓冲在"成功后才裁剪"策略下会持续增长。宁可丢弃最旧数据也不能让进程 OOM 崩溃。
                        if (_fullSampleTimes.Count > MaxBufferPoints * 3)
                        {
                            Log.Error("[实时监视] 缓冲区达硬上限（{Count} 点）且尚未成功落库，丢弃最旧数据防止内存耗尽",
                                _fullSampleTimes.Count);
                            TrimMemoryBuffer();
                        }
                    }

                    // 状态汇总（部分装置失败时提示其余正常）
                    var failedCount = readResults.Count(r => r.Data == null);
                    ConnectionState = failedCount == 0
                        ? "读取正常"
                        : $"读取正常（{failedCount} 台装置失败，详见装置状态）";
                }
                catch (Exception ex)
                {
                    Log.Warning("[实时监视] 更新 UI 失败: {Error}", ex.Message);
                }
            });

            // ========== 阶段 4：后台线程保存数据（不阻塞 UI）==========

            _tickCount++;

            // 定期保存曲线数据（不阻塞 UI，失败不影响实时显示）
            if (currentTick % SaveInterval == 0 && _currentSessionCode != null && _realtimeDataService != null)
            {
                try
                {
                    // 集合由 UI 线程增删，必须在 UI 线程快照，避免后台线程 ToArray() 与之并发（脏读/异常）。
                    // InvokeAsync 异步等待：不阻塞 tick 线程（原同步 Invoke 是周期性卡顿来源）。
                    var legacy = await _uiDispatcher.InvokeAsync(() =>
                        (Pressure: PressurePoints.ToArray(),
                         Flow: FlowPoints.ToArray(),
                         Temp: TempPoints.ToArray(),
                         Count: PressurePoints.Count));
                    double[] pressureSnapshot = legacy.Pressure;
                    double[] flowSnapshot = legacy.Flow;
                    double[] tempSnapshot = legacy.Temp;
                    int pointCount = legacy.Count;

                    await _realtimeDataService.SaveCurveAsync(
                        _currentSessionCode,
                        pressureSnapshot,
                        flowSnapshot,
                        tempSnapshot,
                        pointCount);
                }
                catch (Exception ex)
                {
                    Log.Warning("[实时监视] 保存曲线失败: {Error}", ex.Message);
                    // 只更新 UI 提示
                    _uiDispatcher.BeginInvoke(() =>
                    {
                        ConnectionState = $"读取正常，保存曲线失败：{ex.Message}";
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[实时监视] Tick 异常：{Error}", ex.Message);
            _uiDispatcher.BeginInvoke(() =>
            {
                ConnectionState = $"读取异常：{ex.Message}";
            });
        }
        finally
        {
            // 释放重入标志，允许下一次 tick
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    /// <summary>
    /// 读取单台装置的全部变量。返回 null 表示读取失败（失败计数与重连判定在内部处理）。
    /// </summary>
    private async Task<Dictionary<string, double>?> ReadDeviceAsync(DeviceRuntime d, CancellationToken token)
    {
        try
        {
            var conn = d.Connection;
            if (conn == null) return null;

            if (conn is SiemensS7PlcConnection s7Plc)
            {
                // 西门子 S7 协议读取
                var requests = d.RegisterConfigs
                    .Where(vc => !string.IsNullOrEmpty(vc.SiemensAddress))
                    .Select(vc => new SiemensReadRequest { SiemensAddress = vc.SiemensAddress, DataType = vc.DataType })
                    .ToList();

                if (_tickCount == 0)
                    Log.Information("[实时监视] {Device} Tick 开始，西门子变量数={Count}, 采样间隔={Interval}ms", d.DeviceCode, requests.Count, SampleIntervalMs);

                var result = await s7Plc.ReadMultipleBySiemensAddressAsync(requests, token);
                if (!result.IsSuccess || result.Data == null)
                {
                    await OnDeviceReadFailureAsync(d, result.Error);
                    return null;
                }

                d.ConsecutiveReadFailures = 0;
                return result.Data;
            }

            // Modbus 协议读取
            var modbusRequests = d.RegisterConfigs
                .Select(vc => new PlcRegisterRequest { Address = vc.RegisterAddress, DataType = vc.DataType })
                .ToList();

            if (_tickCount == 0)
                Log.Information("[实时监视] {Device} Tick 开始，寄存器数={Count}, 采样间隔={Interval}ms", d.DeviceCode, modbusRequests.Count, SampleIntervalMs);

            var modbusResult = await conn.ReadMultipleAsync(modbusRequests, token);
            if (!modbusResult.IsSuccess || modbusResult.Data == null)
            {
                await OnDeviceReadFailureAsync(d, modbusResult.Error);
                return null;
            }

            d.ConsecutiveReadFailures = 0;
            // 将寄存器地址转换为字符串 key，统一处理
            return modbusResult.Data.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await OnDeviceReadFailureAsync(d, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 单台装置读取失败处理：失败计数；达到阈值标记该装置死亡并异步重连（只停该装置）。
    /// 所选装置死亡才停止整体监视（独占模型仅一台）。
    /// </summary>
    private Task OnDeviceReadFailureAsync(DeviceRuntime d, string? error)
    {
        d.ConsecutiveReadFailures++;
        if (_tickCount % 10 == 0 || d.ConsecutiveReadFailures >= AutoReconnectThreshold)
            Log.Warning("[实时监视] {Device} 读取失败（连续第 {Count} 次）: {Error}", d.DeviceCode, d.ConsecutiveReadFailures, error);

        if (d.ConsecutiveReadFailures < AutoReconnectThreshold)
        {
            _uiDispatcher.BeginInvoke(() =>
            {
                var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == d.DeviceCode);
                if (item != null)
                    item.Status = $"读取失败（{d.ConsecutiveReadFailures}/{AutoReconnectThreshold}）：{error}";
            });
            return Task.CompletedTask;
        }

        // 达到阈值：标记死亡并异步重连（UI 状态更新与重连都在 UI 线程发起，避免与后台并发）
        d.IsAlive = false;
        _uiDispatcher.BeginInvoke(async () =>
        {
            var item = PlcDevices.FirstOrDefault(p => p.DeviceCode == d.DeviceCode);
            if (item != null)
                item.Status = $"读取连续失败 {d.ConsecutiveReadFailures} 次，正在自动重连...";
            ConnectionState = $"{d.DeviceCode} 读取连续失败，正在自动重连...";

            var reconnected = await TryReconnectDeviceAsync(d);
            if (reconnected)
            {
                d.ConsecutiveReadFailures = 0;
                d.IsAlive = true;
                if (item != null) item.Status = "自动重连成功";
                ConnectionState = $"{d.DeviceCode} 自动重连成功，恢复读取";
                Log.Information("[实时监视] {Device} 自动重连成功", d.DeviceCode);
            }
            else
            {
                if (item != null) item.Status = "连接失败（已暂停该装置）";

                // 只有当前采集的装置死亡才停止整体监视（独占模型仅一台）
                var selected = SelectedDevices();
                if (selected.Count == 0 || selected.All(x => !x.IsAlive))
                {
                    Log.Error("[实时监视] 所有装置自动重连失败，停止监视");
                    ConnectionState = "所有装置连接失败，自动重连未成功，已停止监视";
                    await StopMonitoringAsync();
                }
                else
                {
                    ConnectionState = $"{d.DeviceCode} 连接失败已暂停；其余装置继续监视";
                }
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 添加数据点到曲线通道
    /// 注意：此方法必须在 UI 线程调用
    /// </summary>
    private void AddToChannel(string channel, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        switch (channel)
        {
            case "Pressure":
                PressurePoints.Add(value);
                break;
            case "Flow":
                FlowPoints.Add(value);
                break;
            case "Temp":
                TempPoints.Add(value);
                break;
            case "Flow2":
                Flow2Points.Add(value);
                break;
            case "Pressure2":
                Pressure2Points.Add(value);
                break;
        }
    }

    /// <summary>
    /// 导出曲线数据为 CSV（动态通道：所有用户配置的变量均导出）
    /// </summary>
    // 导出重入保护（AsyncRelayCommand 执行期间会自动禁用按钮，这里是双保险）
    private bool _isExportingCsv;

    [RelayCommand]
    private async Task ExportToCsvAsync()
    {
        // 用全量缓冲导出（不受图表 300 点显示窗口限制，导出整段试验的所有数据）
        if (_isExportingCsv)
        {
            MessageBox.Show("正在导出，请稍候...", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_fullSampleTimes.Count == 0 || _fullChannelData.Count == 0)
        {
            MessageBox.Show("没有可导出的数据，请先开始监视", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                FileName = $"实时数据_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Title = "导出实时数据"
            };

            if (saveDialog.ShowDialog() != true) return;

            _isExportingCsv = true;
            try
            {
                // 快照全量数据与通道顺序（UI 线程，避免与 tick 追加并发），
                // 随后所有构建/换算/写盘转入后台线程——最多 86,400 行在 UI 线程做会明显冻结。
                var shot = SnapshotFull();
                var times = shot.Times;
                // 保持与图例一致的通道顺序（按 Channels），仅取有数据的
                var orderedCodes = Channels
                    .Select(c => _channelByCode.FirstOrDefault(kv => kv.Value == c).Key)
                    .Where(code => code != null && shot.Channels.ContainsKey(code))
                    .ToList();

                int dataRows = await Task.Run(() =>
                {
                    // 动态表头：导出时间 + 每个通道名称（数值按通道单位换算，压力通道导出 kPa）
                    var channelData = new Dictionary<string, double[]>(StringComparer.Ordinal);
                    var headerParts = new List<string> { "\"导出时间\"" };
                    foreach (var code in orderedCodes)
                    {
                        var (name, unit, raw) = shot.Channels[code!];
                        headerParts.Add($"\"{name}({unit})\"");
                        channelData[code!] = Helpers.PressureUnitConverter.ScaleToUnit(raw, unit);
                    }
                    var csvLines = new List<string> { string.Join(",", headerParts) };

                    int maxCount = times.Length;
                    foreach (var code in orderedCodes)
                        maxCount = Math.Max(maxCount, channelData[code!].Length);

                    for (int i = 0; i < maxCount; i++)
                    {
                        var time = i < times.Length ? times[i] : (times.Length > 0 ? times[^1] : DateTime.Now);
                        var rowParts = new List<string> { $"\"{time:yyyy-MM-dd HH:mm:ss}\"" };
                        foreach (var code in orderedCodes)
                        {
                            var data = channelData[code!];
                            rowParts.Add(i < data.Length ? $"{data[i]:F6}" : string.Empty);
                        }
                        csvLines.Add(string.Join(",", rowParts));
                    }

                    File.WriteAllLines(saveDialog.FileName, csvLines, System.Text.Encoding.UTF8);
                    return csvLines.Count - 1;
                });

                MessageBox.Show($"成功导出 {dataRows} 条数据（{orderedCodes.Count} 个通道）", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                _isExportingCsv = false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Log.Error(ex, "导出 CSV 失败");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();

        // 兜底：监视中被释放（如应用关闭）时，同步保存一次已采集的全量数据，避免丢失。
        // 此处刻意保持同步 Invoke：Dispose 无法 await，且关闭期 Dispatcher 随时可能停摆，
        // 失败由下方 catch 记日志（丢兜底保存也不能阻塞退出）。
        if (_isMonitoring && _currentRecordCode != null)
        {
            try
            {
                var shot = _uiDispatcher.CheckAccess() ? SnapshotFull() : _uiDispatcher.Invoke(SnapshotFull);
                if (shot.Times.Length > 0 || shot.Channels.Count > 0)
                    PersistSnapshot(_currentRecordCode, shot.Times, shot.Channels);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[实时监视] Dispose 兜底保存失败");
            }
        }

        _timer.Dispose();  // System.Timers.Timer 需要显式 Dispose
        _readCts?.Cancel();
        _readCts?.Dispose();

        // 同步释放全部装置的 PLC 连接资源（避免 .Wait() 死锁）
        foreach (var d in _devices)
        {
            try { (d.Connection as IDisposable)?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "[实时监视] 释放装置 {Device} 连接资源时发生警告", d.DeviceCode); }
            d.Connection = null;
        }
    }
}
