using System.Collections.ObjectModel;
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
/// 实时监视视图模型
/// </summary>
public sealed partial class RealtimeMonitorViewModel : ViewModelBase, IRefreshable, IDisposable
{
    // 使用 System.Timers.Timer（后台线程），PLC 读数据不阻塞 UI
    private readonly System.Timers.Timer _timer;
    private readonly Dispatcher _uiDispatcher;
    private IModbusPlcConnection? _plcConnection;
    private List<PlcVariableConfig> _registerConfigs = [];
    private PlcConnectionConfig _plcConnectionConfig = new();
    private RealtimeDataService? _realtimeDataService;
    private string? _currentSessionCode;
    private CancellationTokenSource? _readCts;
    private bool _disposed;
    private int _tickCount;
    // Tick 重入标志：0=空闲 1=读取中。读取慢于采样周期时跳过本次，避免并发访问同一 PLC 连接。
    private int _tickRunning;
    private const int SaveInterval = 100; // 每 100 次 tick 保存一次曲线

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
        if (_suppressSelectionCascade) return;
        _ = LoadUnitsAsync(value);
        SelectedUnit = null;
        SelectedObject = null;
    }

    [ObservableProperty]
    private Unit? _selectedUnit;

    partial void OnSelectedUnitChanged(Unit? value)
    {
        if (_suppressSelectionCascade) return;
        _ = LoadObjectsAsync(value);
        SelectedObject = null;
    }

    [ObservableProperty]
    private TestObjectPathNode? _selectedObject;

    // ============ 测量装置选择 ============
    /// <summary>可选的测量装置（来自台账，仅启用状态）</summary>
    public ObservableCollection<MeasurementDevice> AvailableDevices { get; } = [];

    [ObservableProperty]
    private MeasurementDevice? _selectedDevice;

    /// <summary>可编辑的寄存器变量列表（用于 UI 配置）</summary>
    public ObservableCollection<MonitorVariable> MonitorVariables { get; } = [];

    /// <summary>采样时间列表（全量保留，不裁剪）</summary>
    private readonly List<DateTime> _sampleTimes = [];

    // ===== 全量历史缓冲（不裁剪）：用于保存入库与导出，确保保留整段试验的所有数据 =====
    /// <summary>全量采样时间（每个 tick 追加一次，不裁剪）</summary>
    private readonly List<DateTime> _fullSampleTimes = [];
    /// <summary>全量通道数据：变量编码 → 该通道所有采样值（不裁剪）</summary>
    private readonly Dictionary<string, List<double>> _fullChannelData = new();
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
            if (!_disposed && _isMonitoring) await TickAsync();
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
    /// 加载 PLC 寄存器配置
    /// </summary>
    private void LoadPlcConfig()
    {
        try
        {
            var cfg = AppConfiguration.GetPlcRegisters();
            _plcConnectionConfig = cfg.Connection;
            _registerConfigs = cfg.Variables;
            PlcIpAddress = _plcConnectionConfig.IpAddress;
            SampleIntervalMs = cfg.SampleIntervalMs > 0 ? cfg.SampleIntervalMs : 1000;

            // 构建可编辑的 MonitorVariables 列表
            MonitorVariables.Clear();
            Variables.Clear();
            foreach (var vc in _registerConfigs)
            {
                MonitorVariables.Add(MonitorVariable.FromConfig(vc));
                Variables.Add(new RealtimeVariableItem
                {
                    VariableCode = vc.VariableCode,
                    VariableName = vc.VariableName,
                    CurrentValue = "-",
                    Unit = vc.Unit,
                    Channel = $"Reg {vc.RegisterAddress} ({vc.DataType})",
                    UpdatedAt = "-",
                    Status = "待连接",
                    CurveChannel = vc.CurveChannel,
                    MinDisplay = vc.MinDisplay,
                    MaxDisplay = vc.MaxDisplay,
                });
            }

            // 初始化曲线显示范围
            foreach (var vc in _registerConfigs.Where(v => v.CurveChannel != null))
            {
                UpdateChannelRange(vc.CurveChannel!, vc.MinDisplay, vc.MaxDisplay);
            }

            // 构建动态趋势通道（每个变量一条曲线 + 图例项）
            SyncChannelsFromVariables();
        }
        catch (Exception ex)
        {
            ConnectionState = $"配置加载失败：{ex.Message}";
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
    /// 保存寄存器配置
    /// </summary>
    public void SaveConfig()
    {
        _registerConfigs = MonitorVariables.Select(mv => mv.ToConfig()).ToList();
        SavePlcConfigToJson();

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

        ConnectionState = $"✅ 已保存 {_registerConfigs.Count} 个变量配置";
    }

    /// <summary>
    /// 保存 PLC 地址到本地
    /// </summary>
    private void SavePlcIp()
    {
        _plcConnectionConfig.IpAddress = PlcIpAddress;
        SavePlcConfigToJson();
        ConnectionState = $"✅ PLC 地址已保存：{PlcIpAddress}";
    }

    /// <summary>
    /// 将当前配置保存到 plc-registers.json
    /// </summary>
    private void SavePlcConfigToJson()
    {
        try
        {
            var config = new PlcRegistersSection
            {
                Connection = _plcConnectionConfig,
                Variables = _registerConfigs,
                SampleIntervalMs = SampleIntervalMs
            };

            var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plc-registers.json");
            var wrapper = new { PlcRegisters = config };
            var json = System.Text.Json.JsonSerializer.Serialize(wrapper, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(jsonPath, json);
        }
        catch (Exception ex)
        {
            ConnectionState = $"保存配置失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 添加新变量
    /// </summary>
    public void AddVariable()
    {
        // 确保变量名唯一
        int suffix = 1;
        string baseName = "新变量";
        string newName = baseName;
        while (MonitorVariables.Any(mv => mv.VariableName == newName))
        {
            suffix++;
            newName = $"{baseName}{suffix}";
        }

        MonitorVariables.Add(new MonitorVariable
        {
            VariableName = newName,
            RegisterAddress = 0,
            SiemensAddress = "DB15.DBD0",
            DataType = "double",
            Unit = "",
            MinDisplay = 0,
            MaxDisplay = 100,
        });
        // 立即同步通道：新变量马上出现在图例/曲线/读取列表
        SyncChannelsFromVariables();
        RebuildReadonlyVariables();
    }

    /// <summary>
    /// 删除变量
    /// </summary>
    public void RemoveVariable(MonitorVariable? variable)
    {
        if (variable != null)
        {
            MonitorVariables.Remove(variable);
            SyncChannelsFromVariables();
            RebuildReadonlyVariables();
        }
    }

    /// <summary>重建只读变量列表（当前值表格），保留已有当前值。</summary>
    private void RebuildReadonlyVariables()
    {
        // 使用 GroupBy 避免重复的 VariableCode 导致 ToDictionary 异常
        var snapshot = Variables
            .GroupBy(v => v.VariableCode)
            .ToDictionary(g => g.Key, g => (g.First().CurrentValue, g.First().UpdatedAt));
        Variables.Clear();
        foreach (var cfg in _registerConfigs)
        {
            snapshot.TryGetValue(cfg.VariableCode, out var prev);
            // 根据配置显示对应地址：优先西门子地址，其次 Modbus 寄存器地址
            string channelDisplay = !string.IsNullOrEmpty(cfg.SiemensAddress)
                ? $"{cfg.SiemensAddress} ({cfg.DataType})"
                : $"Reg {cfg.RegisterAddress} ({cfg.DataType})";

            Variables.Add(new RealtimeVariableItem
            {
                VariableCode = cfg.VariableCode,
                VariableName = cfg.VariableName,
                CurrentValue = prev.CurrentValue ?? "-",
                Unit = cfg.Unit,
                Channel = channelDisplay,
                UpdatedAt = prev.UpdatedAt ?? "-",
                Status = "待连接",
                CurveChannel = cfg.CurveChannel,
                MinDisplay = cfg.MinDisplay,
                MaxDisplay = cfg.MaxDisplay,
            });
        }
    }

    /// <summary>
    /// 更新曲线通道范围
    /// </summary>
    private void UpdateChannelRange(string channel, double min, double max)
    {
        switch (channel)
        {
            case "Pressure": PressureMin = min; PressureMax = max; break;
            case "Flow": FlowMin = min; FlowMax = max; break;
            case "Temp": TempMin = min; TempMax = max; break;
            case "Flow2": Flow2Min = min; Flow2Max = max; break;
            case "Pressure2": Pressure2Min = min; Pressure2Max = max; break;
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
    /// 根据当前 MonitorVariables 同步动态通道与读取配置。
    /// 新变量立即出现在图例/曲线/读取列表；删除的移除；已存在的保留曲线数据。
    /// </summary>
    private void SyncChannelsFromVariables()
    {
        // 1. 重建读取配置，使 tick 立即读取新变量（不必先点保存）
        _registerConfigs = MonitorVariables.Select(mv => mv.ToConfig()).ToList();

        // 2. 同步通道集合
        var wantedCodes = new HashSet<string>();
        int idx = 0;
        foreach (var cfg in _registerConfigs)
        {
            wantedCodes.Add(cfg.VariableCode);
            if (_channelByCode.TryGetValue(cfg.VariableCode, out var existing))
            {
                // 已存在：更新名称/单位/颜色
                existing.Name = cfg.VariableName;
                existing.Unit = cfg.Unit;
            }
            else
            {
                var ch = new Controls.TrendChannel
                {
                    Name = cfg.VariableName,
                    Unit = cfg.Unit,
                    Color = _palette[idx % _palette.Length],
                };
                _channelByCode[cfg.VariableCode] = ch;
                Channels.Add(ch);
                // 同一个实例按分组加入对应图表集合（压力/温度/流量），使曲线更新自动同步
                GroupCollectionFor(cfg.CurveChannel).Add(ch);
            }
            idx++;
        }

        // 3. 移除已删除的变量对应通道
        foreach (var code in _channelByCode.Keys.ToList())
        {
            if (!wantedCodes.Contains(code))
            {
                var ch = _channelByCode[code];
                Channels.Remove(ch);
                // 从三个分组集合中移除（不在其中则为空操作）
                PressureChannels.Remove(ch);
                TempChannels.Remove(ch);
                FlowChannels.Remove(ch);
                _channelByCode.Remove(code);
            }
        }

        // 4. 关联每个变量行到它的趋势通道（供表格显示颜色、勾选控制显隐）
        foreach (var mv in MonitorVariables)
        {
            var code = mv.ToConfig().VariableCode;
            mv.Channel = _channelByCode.TryGetValue(code, out var ch) ? ch : null;
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
    /// 连接 PLC（自动识别 Modbus 或西门子 S7 协议）
    /// </summary>
    [RelayCommand]
    private async Task ConnectPlcAsync()
    {
        if (IsConnected) return;

        try
        {
            var plcType = (_plcConnectionConfig.PlcType ?? "Modbus").ToUpper();
            var protocol = _plcConnectionConfig.Protocol ?? "tcp";
            var ip = PlcIpAddress;
            var port = _plcConnectionConfig.Port > 0 ? _plcConnectionConfig.Port : (plcType == "SIEMENSS7" ? 102 : 502);

            DeviceResult result;
            bool realUsable = false;

            if (plcType == "SIEMENSS7")
            {
                // ========== 西门子 S7 协议 ==========
                Log.Information("[实时监视] 使用西门子 S7 协议连接 PLC，IP={IP}, Port={Port}, CPU={Protocol}", ip, port, protocol);

                var s7Plc = new SiemensS7PlcConnection(
                    cpuType: protocol,
                    rack: _plcConnectionConfig.Rack,
                    slot: _plcConnectionConfig.Slot);
                result = await s7Plc.ConnectAsync(ip, port);

                if (result.IsSuccess)
                {
                    realUsable = await ProbeS7ReadableAsync(s7Plc);
                }

                if (result.IsSuccess && realUsable)
                {
                    _plcConnection = s7Plc;
                    IsConnected = true;
                    ConnectionState = $"已连接西门子 PLC {ip}:{port} ({protocol})";
                    Log.Information("[实时监视] 已连接西门子 PLC {IP}:{Port} ({Protocol})", ip, port, protocol);
                }
                else
                {
                    var reason = result.IsSuccess
                        ? $"连接成功但读不到有效数据（IP={ip}:{port}, CPU={protocol}, Rack={_plcConnectionConfig.Rack}, Slot={_plcConnectionConfig.Slot}）；请检查西门子变量地址配置及 DB 块是否可读"
                        : result.Error;
                    try { s7Plc.Dispose(); } catch (Exception dex) { Log.Debug(dex, "[实时监视] 释放 S7 连接失败"); }
                    await HandleConnectionFailureAsync("西门子 S7", reason);
                }
            }
            else
            {
                // ========== Modbus 协议（默认） ==========
                Log.Information("[实时监视] 使用 Modbus 协议连接 PLC，IP={IP}, Port={Port}", ip, port);

                var realPlc = new ModbusPlcConnection(protocol);
                result = await realPlc.ConnectAsync(ip, port);

                // 真实连接成功后，试读一次验证能否拿到有效数据
                if (result.IsSuccess)
                {
                    realUsable = await ProbeRealReadableAsync(realPlc);
                }

                if (result.IsSuccess && realUsable)
                {
                    _plcConnection = realPlc;
                    IsConnected = true;
                    ConnectionState = $"已连接 PLC {protocol}://{ip}:{port}";
                    Log.Information("[实时监视] 已连接 PLC {Protocol}://{IP}:{Port}", protocol, ip, port);
                }
                else
                {
                    var reason = result.IsSuccess
                        ? $"连接成功但读不到有效数据（{protocol}://{ip}:{port}）；TCP 可达但可能无 Modbus 服务，或寄存器地址配置有误"
                        : result.Error;
                    try { realPlc.Dispose(); } catch (Exception dex) { Log.Debug(dex, "[实时监视] 释放 Modbus 连接失败"); }
                    await HandleConnectionFailureAsync("Modbus", reason);
                }
            }
        }
        catch (Exception ex)
        {
            ConnectionState = $"连接异常：{ex.Message}";
            // 记录完整异常（含堆栈、内部异常）到 logs 日志
            Log.Error(ex, "[实时监视] 连接 PLC 异常");
        }
    }

    /// <summary>
    /// 统一处理 PLC 连接失败：
    /// 1) 始终把失败原因（含配置详情）以 Error 级别写入 logs 日志，方便排查现场连接问题；
    /// 2) 默认不再静默降级为仿真数据（AllowSimulationFallback=false），连接失败直接报错，
    ///    使用户能看到并调试真实的连接问题；仅当显式开启仿真降级时才连接模拟 PLC。
    /// </summary>
    private async Task HandleConnectionFailureAsync(string protocolLabel, string reason)
    {
        Log.Error("[实时监视] {Protocol} PLC 连接失败：{Reason}", protocolLabel, reason);

        if (!_plcConnectionConfig.AllowSimulationFallback)
        {
            // 不降级：保持未连接状态并把原因显示到界面，同时已写入 logs
            IsConnected = false;
            _plcConnection = null;
            ConnectionState = $"连接失败：{reason}（详情见 logs 日志）";
            return;
        }

        // 显式开启了仿真降级（演示/无 PLC 环境）
        Log.Warning("[实时监视] 已启用仿真降级（AllowSimulationFallback=true），改用模拟 PLC 数据");
        _plcConnection = new MockPlcConnection();
        var mockResult = await _plcConnection.ConnectAsync("127.0.0.1", 502);
        if (mockResult.IsSuccess)
        {
            IsConnected = true;
            ConnectionState = "[模拟] 已连接（仿真数据演示模式）";
            Log.Information("[实时监视] 已连接模拟 PLC");
        }
        else
        {
            IsConnected = false;
            _plcConnection = null;
            ConnectionState = $"连接失败：{reason}（详情见 logs 日志）";
        }
    }

    /// <summary>
    /// 试读一次寄存器，判断真实连接是否能拿到有效数据。
    /// 全部为 NaN（读取失败）则视为不可用（只是 TCP 通但无 Modbus 服务/PLC）。
    /// </summary>
    private async Task<bool> ProbeRealReadableAsync(IModbusPlcConnection plc)
    {
        try
        {
            var requests = _registerConfigs
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
    private async Task<bool> ProbeS7ReadableAsync(SiemensS7PlcConnection plc)
    {
        try
        {
            // 构建西门子地址读取请求
            var requests = _registerConfigs
                .Where(vc => !string.IsNullOrEmpty(vc.SiemensAddress))
                .Select(vc => new SiemensReadRequest { SiemensAddress = vc.SiemensAddress, DataType = vc.DataType })
                .ToList();

            if (requests.Count == 0)
            {
                // 如果没有配置西门子地址，尝试用第一个变量的寄存器地址兼容读取
                var firstConfig = _registerConfigs.FirstOrDefault();
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
    /// 断开 PLC
    /// </summary>
    [RelayCommand]
    private async Task DisconnectPlcAsync()
    {
        if (IsMonitoring)
        {
            await StopMonitoringAsync();
        }

        try
        {
            if (_plcConnection != null)
            {
                await _plcConnection.DisconnectAsync();
                _plcConnection = null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "断开 PLC 连接时发生警告");
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
            // 校验测量装置选择：必须选中台账中真实存在的装置。
            // 不能写死编号：若该编号不在 MeasurementDevices 台账里，
            // TestRecord 会触发外键 FK_TestRecords_MeasurementDevices_DeviceCode 失败，
            // 连带同批插入的 TestProcessData 也一起回滚（曾表现为两条外键冲突）。
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

            var deviceCode = SelectedDevice.DeviceCode;

            // 创建试验记录
            using var context = DbContextFactory.CreateDbContext();
            var recordCode = $"{SelectedProject.Code}_{SelectedUnit.Code}_{SelectedObject.Code}_{DateTime.Now:yyyyMMddHHmmss}";

            Log.Information("[实时监视] 生成记录编码：{RecordCode}", recordCode);

            var testRecord = new TestRecord
            {
                RecordCode = recordCode,
                ProjectCode = SelectedProject.Code,
                UnitCode = SelectedUnit.Code,
                ObjectCode = SelectedObject.Code,
                ObjectName = SelectedObject.Name,
                ObjectType = SelectedObject.NodeType,
                DeviceCode = deviceCode, // 用户选择的台账装置
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

            IsMonitoring = true;
            _tickCount = 0;
            _readCts = new CancellationTokenSource();

            // 清空曲线和采样时间
            PressurePoints.Clear();
            FlowPoints.Clear();
            TempPoints.Clear();
            Flow2Points.Clear();
            Pressure2Points.Clear();
            TimeAxisPoints.Clear();
            // 清空所有动态通道曲线
            foreach (var ch in Channels) ch.Points.Clear();
            _sampleTimes.Clear();
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
            var shot = _uiDispatcher.CheckAccess() ? SnapshotFull() : _uiDispatcher.Invoke(SnapshotFull);

            if (shot.Times.Length == 0 && shot.Channels.Count == 0) return;

            await Task.Run(() => PersistSnapshot(recordCode!, shot.Times, shot.Channels));
            Log.Information("[实时监视] 已保存全量数据：{N} 采样点, {C} 通道, Record={Code}",
                shot.Times.Length, shot.Channels.Count, recordCode);
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
            channelsDict[code] = new ChannelData
            {
                Name = s.Name, Unit = s.Unit, Data = s.Data,
                Min = s.Data.Length > 0 ? s.Data.Min() : 0,
                Max = s.Data.Length > 0 ? s.Data.Max() : 0,
            };
        }

        // 旧格式固定通道（向后兼容）：按 CurveChannel 从全量数据取对应变量
        double[] FullByCurve(string curve)
        {
            var cfg = _registerConfigs.FirstOrDefault(c =>
                string.Equals(c.CurveChannel, curve, StringComparison.OrdinalIgnoreCase));
            return cfg != null && snap.TryGetValue(cfg.VariableCode, out var s) ? s.Data : [];
        }
        var pressureArray = FullByCurve("Pressure");
        var flowArray = FullByCurve("Flow");
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
            if (pressureArray.Length > 0) testRecord.TestPressure = (decimal)pressureArray.Average();
            context.SaveChanges();
        }
    }

    /// <summary>
    /// 定时器回调：在后台线程读取 PLC 寄存器，UI 线程更新界面
    /// 架构：后台读取 → 数据准备 → UI 线程批量更新
    /// 彻底解决 PLC 通信延迟阻塞 UI 的问题
    /// 支持 Modbus 和西门子 S7 两种协议
    /// </summary>
    private async Task TickAsync()
    {
        // 重入保护：上一次读取尚未完成则跳过本次，避免多个线程池线程并发读写同一 PLC 连接（打乱报文帧）。
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;

        try
        {
            if (_plcConnection == null || _readCts == null) return;

            var cts = _readCts;
            if (cts.IsCancellationRequested) return;

            // ========== 阶段 1：后台线程读取 PLC ==========
            // 所有 IO 操作都在后台线程完成，不阻塞 UI

            Dictionary<string, double> data;

            // 判断 PLC 类型，选择不同的读取方式
            var isSiemensS7 = _plcConnection is SiemensS7PlcConnection;

            if (isSiemensS7)
            {
                // ========== 西门子 S7 协议读取 ==========
                var s7Plc = (_plcConnection as SiemensS7PlcConnection)!;

                // 构建西门子地址读取请求
                var requests = _registerConfigs
                    .Where(vc => !string.IsNullOrEmpty(vc.SiemensAddress))
                    .Select(vc => new SiemensReadRequest { SiemensAddress = vc.SiemensAddress, DataType = vc.DataType })
                    .ToList();

                if (_tickCount == 0)
                    Log.Information("[实时监视] Tick 开始，西门子变量数={Count}, 采样间隔={Interval}ms", requests.Count, SampleIntervalMs);

                var result = await s7Plc.ReadMultipleBySiemensAddressAsync(requests, cts.Token);

                if (!result.IsSuccess || result.Data == null)
                {
                    if (_tickCount % 10 == 0)
                        Log.Warning("[实时监视] 读取失败: {Error}", result.Error);

                    _uiDispatcher.BeginInvoke(() =>
                    {
                        ConnectionState = $"读取失败：{result.Error}";
                    });
                    return;
                }

                data = result.Data;

                if (_tickCount == 0)
                    Log.Information("[实时监视] 读取成功，数据点数={Count}", data.Count);
            }
            else
            {
                // ========== Modbus 协议读取（兼容旧代码） ==========
                var requests = _registerConfigs
                    .Select(vc => new PlcRegisterRequest { Address = vc.RegisterAddress, DataType = vc.DataType })
                    .ToList();

                if (_tickCount == 0)
                    Log.Information("[实时监视] Tick 开始，寄存器数={Count}, 采样间隔={Interval}ms", requests.Count, SampleIntervalMs);

                var result = await _plcConnection.ReadMultipleAsync(requests, cts.Token);

                if (!result.IsSuccess || result.Data == null)
                {
                    if (_tickCount % 10 == 0)
                        Log.Warning("[实时监视] 读取失败: {Error}", result.Error);

                    _uiDispatcher.BeginInvoke(() =>
                    {
                        ConnectionState = $"读取失败：{result.Error}";
                    });
                    return;
                }

                // 将寄存器地址转换为字符串 key，统一处理
                data = result.Data.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);

                if (_tickCount == 0)
                    Log.Information("[实时监视] 读取成功，数据点数={Count}", data.Count);
            }

            // ========== 阶段 2：后台线程准备数据 ==========
            // 所有计算、转换都在后台线程完成

            var uiUpdateList = new List<(string code, string name, string strVal, string status, double? rawValue, string? curveChannel)>();
            foreach (var vc in _registerConfigs)
            {
                // 根据协议类型选择查找 key
                string lookupKey = isSiemensS7 ? vc.SiemensAddress : vc.RegisterAddress.ToString();
                bool hasValue = data.TryGetValue(lookupKey, out var value) && !double.IsNaN(value) && !double.IsInfinity(value);

                if (hasValue)
                {
                    var strVal = (vc.DataType == "ushort" || vc.DataType == "word" || vc.DataType == "int")
                        ? ((ushort)value).ToString()
                        : (vc.DataType == "dword" || vc.DataType == "uint")
                            ? ((uint)value).ToString()
                            : value.ToString("F4");

                    uiUpdateList.Add((vc.VariableCode, vc.VariableName, strVal, "正常", value, vc.CurveChannel));

                    if (_tickCount < 3)
                        Log.Information("[实时监视] {Addr}({Code}) 值{Value}", lookupKey, vc.VariableCode, strVal);
                }
                else
                {
                    if (_tickCount < 3)
                        Log.Warning("[实时监视] {Addr} 未返回有效数据", lookupKey);
                    uiUpdateList.Add((vc.VariableCode, vc.VariableName, "-", "未读取到数据", null, vc.CurveChannel));
                }
            }

            var currentTick = _tickCount + 1;

            // ========== 阶段 3：切回 UI 线程批量更新 ==========
            // 只在这里更新界面，不做任何 IO 操作

            var sampleTime = DateTime.Now;

            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 记录采样时间（保留全量，不裁剪）
                    _sampleTimes.Add(sampleTime);

                    // 全量采样时间（不裁剪，用于保存/导出保留整段数据）
                    _fullSampleTimes.Add(sampleTime);

                    // 推进 X 轴时间：计算相对监视开始时间的秒数偏移（避免 DateTimeAxis.ToDouble 的大数字）
                    // 必须先于通道数据更新，使图表重绘时 X 轴已是最新窗口。
                    double relativeSeconds = (sampleTime - _monitorStartTime).TotalSeconds;
                    TimeAxisPoints.Add(relativeSeconds);
                    _sampleSeq++;

                    // 批量更新变量列表 + 动态通道曲线/图例
                    foreach (var (code, name, strVal, status, rawValue, curveChannel) in uiUpdateList)
                    {
                        var item = Variables.FirstOrDefault(v => v.VariableCode == code);
                        var mv = MonitorVariables.FirstOrDefault(m => m.VariableName == name);

                        if (item != null)
                        {
                            item.CurrentValue = strVal;
                            item.UpdatedAt = sampleTime.ToString("HH:mm:ss");
                            item.Status = status;
                        }
                        if (mv != null)
                        {
                            mv.CurrentValue = strVal;
                            mv.UpdatedAt = sampleTime.ToString("HH:mm:ss");
                            mv.Status = status;
                        }

                        // 喂动态通道：每个变量一条曲线，图例显示当前值
                        if (_channelByCode.TryGetValue(code, out var ch))
                        {
                            ch.CurrentValue = strVal;
                            if (rawValue.HasValue)
                            {
                                // 显示曲线：全量保留、不裁剪（左侧滚出屏幕但数据仍在，可拖回查看）
                                ch.Points.Add(rawValue.Value);

                                // 全量缓冲（不裁剪）：按变量编码累积该通道所有采样值
                                if (!_fullChannelData.TryGetValue(code, out var full))
                                {
                                    full = [];
                                    _fullChannelData[code] = full;
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

                    // 更新曲线标题状态（每 10 个采样点更新一次计数显示）
                    if (currentTick % 10 == 0)
                    {
                        OnPropertyChanged(nameof(CurveInfoText));
                    }

                    // 周期自动保存：即使用户未点“停止监视”就切走或关闭，也能保住已采集的全量数据。
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
                    }

                    ConnectionState = "读取正常";
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
                    // 集合由 UI 线程增删，必须在 UI 线程快照，避免后台线程 ToArray() 与之并发（脏读/异常）
                    double[] pressureSnapshot = [];
                    double[] flowSnapshot = [];
                    double[] tempSnapshot = [];
                    int pointCount = 0;
                    _uiDispatcher.Invoke(() =>
                    {
                        pressureSnapshot = PressurePoints.ToArray();
                        flowSnapshot = FlowPoints.ToArray();
                        tempSnapshot = TempPoints.ToArray();
                        pointCount = PressurePoints.Count;
                    });

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
            Log.Error(ex, "[实时监视] Tick 异常: {Error}", ex.Message);
            _uiDispatcher.BeginInvoke(async () =>
            {
                ConnectionState = $"读取失败：{ex.Message}";
                await StopMonitoringAsync();
            });
        }
        finally
        {
            // 释放重入标志，允许下一次 tick
            Interlocked.Exchange(ref _tickRunning, 0);
        }
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
    [RelayCommand]
    private void ExportToCsv()
    {
        // 用全量缓冲导出（不受图表 300 点显示窗口限制，导出整段试验的所有数据）
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

            // 快照全量数据（UI 线程），避免与 tick 并发
            var shot = SnapshotFull();
            var times = shot.Times;
            // 保持与图例一致的通道顺序（按 Channels），仅取有数据的
            var orderedCodes = Channels
                .Select(c => _channelByCode.FirstOrDefault(kv => kv.Value == c).Key)
                .Where(code => code != null && shot.Channels.ContainsKey(code))
                .ToList();

            // 动态表头：导出时间 + 每个通道名称
            var headerParts = new List<string> { "\"导出时间\"" };
            foreach (var code in orderedCodes)
            {
                var (name, unit, _) = shot.Channels[code!];
                headerParts.Add($"\"{name}({unit})\"");
            }
            var csvLines = new List<string> { string.Join(",", headerParts) };

            int maxCount = times.Length;
            foreach (var code in orderedCodes)
                maxCount = Math.Max(maxCount, shot.Channels[code!].Data.Length);

            for (int i = 0; i < maxCount; i++)
            {
                var time = i < times.Length ? times[i] : (times.Length > 0 ? times[^1] : DateTime.Now);
                var rowParts = new List<string> { $"\"{time:yyyy-MM-dd HH:mm:ss}\"" };
                foreach (var code in orderedCodes)
                {
                    var data = shot.Channels[code!].Data;
                    rowParts.Add(i < data.Length ? $"{data[i]:F6}" : string.Empty);
                }
                csvLines.Add(string.Join(",", rowParts));
            }

            File.WriteAllLines(saveDialog.FileName, csvLines, System.Text.Encoding.UTF8);
            MessageBox.Show($"成功导出 {csvLines.Count - 1} 条数据（{orderedCodes.Count} 个通道）", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

        // 兜底：监视中被释放（如应用关闭）时，同步保存一次已采集的全量数据，避免丢失
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

        // 同步释放 PLC 资源（避免 .Wait() 死锁）
        try { (_plcConnection as IDisposable)?.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "释放 PLC 连接资源时发生警告"); }
        _plcConnection = null;
    }
}
