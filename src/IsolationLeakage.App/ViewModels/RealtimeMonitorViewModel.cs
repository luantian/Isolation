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

    // TrendChart 数据源（支持批量操作，减少事件触发）
    public BulkObservableCollection<double> PressurePoints { get; } = [];
    public BulkObservableCollection<double> FlowPoints { get; } = [];
    public BulkObservableCollection<double> TempPoints { get; } = [];

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

        // 重建只读 Variables 列表
        Variables.Clear();
        foreach (var cfg in _registerConfigs)
        {
            Variables.Add(new RealtimeVariableItem
            {
                VariableCode = cfg.VariableCode,
                VariableName = cfg.VariableName,
                CurrentValue = Variables.FirstOrDefault(v => v.VariableCode == cfg.VariableCode)?.CurrentValue ?? "-",
                Unit = cfg.Unit,
                Channel = $"Reg {cfg.RegisterAddress} ({cfg.DataType})",
                UpdatedAt = Variables.FirstOrDefault(v => v.VariableCode == cfg.VariableCode)?.UpdatedAt ?? "-",
                Status = "待连接",
                CurveChannel = cfg.CurveChannel,
                MinDisplay = cfg.MinDisplay,
                MaxDisplay = cfg.MaxDisplay,
            });
        }

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
    }

    /// <summary>
    /// 删除变量
    /// </summary>
    public void RemoveVariable(MonitorVariable? variable)
    {
        if (variable != null)
        {
            MonitorVariables.Remove(variable);
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
        }
    }

    // 曲线范围属性
    [ObservableProperty] private double _pressureMin;
    [ObservableProperty] private double _pressureMax = 1;
    [ObservableProperty] private double _flowMin;
    [ObservableProperty] private double _flowMax = 1;
    [ObservableProperty] private double _tempMin;
    [ObservableProperty] private double _tempMax = 1;

    // 变量列表
    public ObservableCollection<RealtimeVariableItem> Variables { get; } = [];

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

            if (result.IsSuccess)
            {
                _plcConnection = realPlc;
                IsConnected = true;
                ConnectionState = $"已连接 PLC {protocol}://{ip}:{port}";
                Log.Information("[实时监视] 已连接 PLC {Protocol}://{IP}:{Port}", protocol, ip, port);
            }
            else
            {
                // 真实连接失败，降级为模拟 PLC
                Log.Warning("[实时监视] 真实 PLC 连接失败 ({Error})，降级为模拟模式", result.Error);
                _plcConnection = new MockPlcConnection();
                var mockResult = await _plcConnection.ConnectAsync("127.0.0.1", 502);

                if (mockResult.IsSuccess)
                {
                    IsConnected = true;
                    ConnectionState = "[模拟] 已连接（仿真数据演示模式）";
                    PlcIpAddress = "127.0.0.1";
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

            // 清空曲线
            PressurePoints.Clear();
            FlowPoints.Clear();
            TempPoints.Clear();

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

            var uiUpdateList = new List<(string code, string strVal, string status, double? curveValue)>();
            foreach (var vc in _registerConfigs)
            {
                if (data.TryGetValue(vc.RegisterAddress, out var value))
                {
                    var strVal = vc.DataType == "ushort"
                        ? ((ushort)value).ToString()
                        : value.ToString("F4");
                    var nowStr = DateTime.Now.ToString("HH:mm:ss");

                    uiUpdateList.Add((vc.VariableCode, strVal, "正常", vc.CurveChannel != null ? value : null));

                    if (vc.CurveChannel != null && _tickCount < 3)
                        Log.Information("[实时监视] 寄存器{Addr} → 通道{Channel} 值{Value}", vc.RegisterAddress, vc.CurveChannel, strVal);
                }
                else
                {
                    if (_tickCount < 3)
                        Log.Warning("[实时监视] 寄存器{Addr} 未返回数据", vc.RegisterAddress);
                    uiUpdateList.Add((vc.VariableCode, "-", "未读取到数据", null));
                }
            }

            var currentTick = _tickCount + 1;

            // ========== 阶段 3：切回 UI 线程批量更新 ==========
            // 只在这里更新界面，不做任何 IO 操作

            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 批量更新变量列表
                    foreach (var (code, strVal, status, curveValue) in uiUpdateList)
                    {
                        var item = Variables.FirstOrDefault(v => v.VariableCode == code);
                        var mv = MonitorVariables.FirstOrDefault(m => m.VariableName == code);

                        if (item != null)
                        {
                            item.CurrentValue = strVal;
                            item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                            item.Status = status;
                        }
                        if (mv != null)
                        {
                            mv.CurrentValue = strVal;
                            mv.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                            mv.Status = status;
                        }

                        // 添加到曲线通道
                        if (curveValue.HasValue)
                        {
                            AddToChannel(code, curveValue.Value);
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
