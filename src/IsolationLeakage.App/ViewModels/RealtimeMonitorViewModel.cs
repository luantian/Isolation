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
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 实时监视变量（支持 UI 编辑）
/// </summary>
public sealed class MonitorVariable : ObservableObject
{
    private string _variableName = string.Empty;
    private int _registerAddress;
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
    public int RegisterAddress
    {
        get => _registerAddress;
        set => SetProperty(ref _registerAddress, value);
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

    /// <summary>转为配置对象</summary>
    public PlcVariableConfig ToConfig() => new()
    {
        VariableCode = VariableName.Replace(" ", "_").ToUpper(),
        VariableName = VariableName,
        RegisterAddress = RegisterAddress,
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
public sealed partial class RealtimeMonitorViewModel : ViewModelBase, IDisposable
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
    private const int MaxPoints = 300;
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

    /// <summary>趋势曲线标题描述文本</summary>
    public string CurveInfoText => $"采样周期 {SampleIntervalMs}ms · 窗口 {MaxPoints} 点 · 已采 {PressurePoints.Count} 点";

    [ObservableProperty]
    private string _sessionInfo = "未开始监视";

    [ObservableProperty]
    private string _plcIpAddress = "127.0.0.1";

    /// <summary>可编辑的寄存器变量列表（用于 UI 配置）</summary>
    public ObservableCollection<MonitorVariable> MonitorVariables { get; } = [];

    /// <summary>采样时间列表（用于导出）</summary>
    private readonly List<DateTime> _sampleTimes = [];

    public RealtimeMonitorViewModel()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        // 加载 PLC 寄存器配置
        LoadPlcConfig();
        Log.Information("[实时监视] 初始化完成，寄存器数={Count}, IP={IP}", _registerConfigs.Count, PlcIpAddress);

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
        MonitorVariables.Add(new MonitorVariable
        {
            VariableName = "新变量",
            RegisterAddress = 0,
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
        var snapshot = Variables.ToDictionary(v => v.VariableCode, v => (v.CurrentValue, v.UpdatedAt));
        Variables.Clear();
        foreach (var cfg in _registerConfigs)
        {
            snapshot.TryGetValue(cfg.VariableCode, out var prev);
            Variables.Add(new RealtimeVariableItem
            {
                VariableCode = cfg.VariableCode,
                VariableName = cfg.VariableName,
                CurrentValue = prev.CurrentValue ?? "-",
                Unit = cfg.Unit,
                Channel = $"Reg {cfg.RegisterAddress} ({cfg.DataType})",
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
    /// 动态趋势通道：每个监控变量一条曲线 + 一个图例项（绑定到 TrendChart.Channels 和图例）。
    /// </summary>
    public ObservableCollection<Controls.TrendChannel> Channels { get; } = [];

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
                _channelByCode.Remove(code);
            }
        }
    }

    public string BoundaryNote => "通过 Modbus TCP 读取 PLC 实时变量；不在本软件中下发试验任务或执行现场控制。";

    // ========== 命令 ==========

    /// <summary>保存配置</summary>
    public ICommand SaveConfigCommand => new RelayCommand(SaveConfig);
    /// <summary>添加变量</summary>
    public ICommand AddVariableCommand => new RelayCommand(AddVariable);
    /// <summary>删除选中变量</summary>
    public ICommand RemoveVariableCommand => new RelayCommand(() => RemoveVariable(SelectedMonitorVariable));
    /// <summary>保存 PLC 地址</summary>
    public ICommand SavePlcIpCommand => new RelayCommand(SavePlcIp);

    [ObservableProperty]
    private MonitorVariable? _selectedMonitorVariable;

    /// <summary>
    /// 连接 PLC
    /// </summary>
    [RelayCommand]
    private async Task ConnectPlcAsync()
    {
        if (IsConnected) return;

        try
        {
            // 优先尝试真实 Modbus TCP 连接
            var protocol = _plcConnectionConfig.Protocol ?? "tcp";
            var ip = PlcIpAddress;
            var port = _plcConnectionConfig.Port > 0 ? _plcConnectionConfig.Port : 502;

            var realPlc = new ModbusPlcConnection(protocol);
            var result = await realPlc.ConnectAsync(ip, port);

            // 真实连接成功后，试读一次验证能否拿到有效数据；
            // 若全部读不到（NaN，说明只是 TCP 通但没有 Modbus 服务/PLC），降级到模拟。
            bool realUsable = false;
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
                // 真实连接失败或读不到有效数据，降级为模拟 PLC
                var reason = result.IsSuccess ? "连接成功但读不到有效数据" : result.Error;
                Log.Warning("[实时监视] 真实 PLC 不可用（{Reason}），降级为模拟模式", reason);
                try { (realPlc as IDisposable)?.Dispose(); } catch { }

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
                    ConnectionState = $"连接失败：{result.Error}";
                    _plcConnection = null;
                }
            }
        }
        catch (Exception ex)
        {
            ConnectionState = $"连接异常：{ex.Message}";
            Log.Warning("[实时监视] 连接异常：{Message}", ex.Message);
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
            if (!probe.IsSuccess || probe.Data == null || probe.Data.Count == 0) return false;

            // 工作正常的 PLC 应该每个通道都返回有效数值；
            // 只要有任一通道是 NaN（读取失败），就判定真实连接不可用，降级到模拟。
            return probe.Data.Values.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
        }
        catch
        {
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
        if (!IsConnected || IsMonitoring) return;

        try
        {
            _realtimeDataService = AppServices.RealtimeDataService;
            var session = await _realtimeDataService.CreateSessionAsync(
                sampleIntervalMs: SampleIntervalMs);
            _currentSessionCode = session.SessionCode;
            SessionInfo = $"会话：{_currentSessionCode}";

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

            // 启动定时器
            _timer.Start();
        }
        catch (Exception ex)
        {
            ConnectionState = $"启动失败：{ex.Message}";
            IsMonitoring = false;
        }
    }

    /// <summary>
    /// 停止监视
    /// </summary>
    [RelayCommand]
    private async Task StopMonitoringAsync()
    {
        if (!IsMonitoring) return;

        IsMonitoring = false;
        _timer.Stop();
        _readCts?.Cancel();

        // 保存最终曲线数据
        if (_currentSessionCode != null && _realtimeDataService != null)
        {
            try
            {
                await _realtimeDataService.SaveCurveAsync(
                    _currentSessionCode,
                    PressurePoints.ToArray(),
                    FlowPoints.ToArray(),
                    TempPoints.ToArray(),
                    PressurePoints.Count,
                    DateTime.Now);

                SessionInfo = $"会话已结束：{_currentSessionCode}（{PressurePoints.Count} 个采样点）";
            }
            catch (Exception ex)
            {
                ConnectionState = $"保存曲线失败：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// 定时器回调：在后台线程读取 PLC 寄存器，UI 线程更新界面
    /// 架构：后台读取 → 数据准备 → UI 线程批量更新
    /// 彻底解决 PLC 通信延迟阻塞 UI 的问题
    /// </summary>
    private async Task TickAsync()
    {
        if (_plcConnection == null || _readCts == null) return;

        try
        {
            var cts = _readCts;
            if (cts.IsCancellationRequested) return;

            // ========== 阶段 1：后台线程读取 PLC ==========
            // 所有 IO 操作都在后台线程完成，不阻塞 UI

            // 构建读取请求
            var requests = _registerConfigs
                .Select(vc => new PlcRegisterRequest { Address = vc.RegisterAddress, DataType = vc.DataType })
                .ToList();

            if (_tickCount == 0)
                Log.Information("[实时监视] Tick 开始，寄存器数={Count}, 采样间隔={Interval}ms", requests.Count, SampleIntervalMs);

            // 批量读取所有寄存器（在后台线程执行，PLC 网络 IO 不阻塞 UI）
            var result = await _plcConnection.ReadMultipleAsync(requests, cts.Token);

            if (!result.IsSuccess || result.Data == null)
            {
                if (_tickCount % 10 == 0)
                    Log.Warning("[实时监视] 读取失败: {Error}", result.Error);

                // 更新 UI（切回 UI 线程）
                _uiDispatcher.BeginInvoke(() =>
                {
                    ConnectionState = $"读取失败：{result.Error}";
                });
                return;
            }

            var data = result.Data;

            if (_tickCount == 0)
                Log.Information("[实时监视] 读取成功，数据点数={Count}", data.Count);

            // ========== 阶段 2：后台线程准备数据 ==========
            // 所有计算、转换都在后台线程完成

            var uiUpdateList = new List<(string code, string name, string strVal, string status, double? rawValue)>();
            foreach (var vc in _registerConfigs)
            {
                if (data.TryGetValue(vc.RegisterAddress, out var value) && !double.IsNaN(value) && !double.IsInfinity(value))
                {
                    var strVal = vc.DataType == "ushort"
                        ? ((ushort)value).ToString()
                        : value.ToString("F4");

                    uiUpdateList.Add((vc.VariableCode, vc.VariableName, strVal, "正常", value));

                    if (_tickCount < 3)
                        Log.Information("[实时监视] 寄存器{Addr}({Code}) 值{Value}", vc.RegisterAddress, vc.VariableCode, strVal);
                }
                else
                {
                    if (_tickCount < 3)
                        Log.Warning("[实时监视] 寄存器{Addr} 未返回有效数据", vc.RegisterAddress);
                    uiUpdateList.Add((vc.VariableCode, vc.VariableName, "-", "未读取到数据", null));
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
                    // 记录采样时间
                    _sampleTimes.Add(sampleTime);

                    // 推进 X 轴采样序号（单调递增，与通道等长同步裁剪）。
                    // 必须先于通道数据更新，使图表重绘时 X 轴已是最新窗口。
                    TimeAxisPoints.Add(_sampleSeq);
                    _sampleSeq++;
                    if (TimeAxisPoints.Count > MaxPoints) TimeAxisPoints.RemoveAt(0);

                    // 批量更新变量列表 + 动态通道曲线/图例
                    foreach (var (code, name, strVal, status, rawValue) in uiUpdateList)
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
                                ch.Points.Add(rawValue.Value);
                                if (ch.Points.Count > MaxPoints) ch.Points.RemoveAt(0);
                            }
                        }
                    }

                    // 更新曲线标题状态（每 10 个采样点更新一次计数显示）
                    if (currentTick % 10 == 0)
                    {
                        OnPropertyChanged(nameof(CurveInfoText));
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

            // 定期保存曲线数据（不阻塞 UI，失败不影响实时显示
            if (currentTick % SaveInterval == 0 && _currentSessionCode != null && _realtimeDataService != null)
            {
                try
                {
                    await _realtimeDataService.SaveCurveAsync(
                        _currentSessionCode,
                        PressurePoints.ToArray(),
                        FlowPoints.ToArray(),
                        TempPoints.ToArray(),
                        PressurePoints.Count);
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
                if (PressurePoints.Count > MaxPoints) PressurePoints.RemoveAt(0);
                break;
            case "Flow":
                FlowPoints.Add(value);
                if (FlowPoints.Count > MaxPoints) FlowPoints.RemoveAt(0);
                break;
            case "Temp":
                TempPoints.Add(value);
                if (TempPoints.Count > MaxPoints) TempPoints.RemoveAt(0);
                break;
            case "Flow2":
                Flow2Points.Add(value);
                if (Flow2Points.Count > MaxPoints) Flow2Points.RemoveAt(0);
                break;
            case "Pressure2":
                Pressure2Points.Add(value);
                if (Pressure2Points.Count > MaxPoints) Pressure2Points.RemoveAt(0);
                break;
        }
    }

    /// <summary>
    /// 导出曲线数据为 CSV（甲方格式）
    /// </summary>
    [RelayCommand]
    private void ExportToCsv()
    {
        if (PressurePoints.Count == 0 && FlowPoints.Count == 0 && TempPoints.Count == 0
            && Flow2Points.Count == 0 && Pressure2Points.Count == 0)
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

            // 生成 CSV 内容（甲方格式）
            var csvLines = new List<string>
            {
                // 表头
                "\"导出时间\",\"实时压力P1\",\"瞬时流量M1\",\"瞬时流量M2\",\"温度T_R\",\"压力P2_R\""
            };

            // 数据行（5 通道，与甲方导出格式一致）
            int maxCount = new[] { PressurePoints.Count, FlowPoints.Count, Flow2Points.Count, TempPoints.Count, Pressure2Points.Count }.Max();
            for (int i = 0; i < maxCount; i++)
            {
                var time = i < _sampleTimes.Count ? _sampleTimes[i] : DateTime.Now;
                double pressureP1 = i < PressurePoints.Count ? PressurePoints[i] : 0.0;
                double flowM1 = i < FlowPoints.Count ? FlowPoints[i] : 0.0;
                double flowM2 = i < Flow2Points.Count ? Flow2Points[i] : 0.0;
                double tempTR = i < TempPoints.Count ? TempPoints[i] : 0.0;
                double pressureP2R = i < Pressure2Points.Count ? Pressure2Points[i] : 0.0;

                csvLines.Add($"\"{time:yyyy-MM-dd HH:mm:ss}\",{pressureP1:F6},{flowM1:F6},{flowM2:F6},{tempTR:F6},{pressureP2R:F6}");
            }

            File.WriteAllLines(saveDialog.FileName, csvLines, System.Text.Encoding.UTF8);
            MessageBox.Show($"成功导出 {csvLines.Count - 1} 条数据", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _timer.Dispose();  // System.Timers.Timer 需要显式 Dispose
        _readCts?.Cancel();
        _readCts?.Dispose();

        // 同步释放 PLC 资源（避免 .Wait() 死锁）
        try { (_plcConnection as IDisposable)?.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "释放 PLC 连接资源时发生警告"); }
        _plcConnection = null;
    }
}
