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
    private readonly DispatcherTimer _timer;
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

    // TrendChart 数据源
    public ObservableCollection<double> PressurePoints { get; } = [];
    public ObservableCollection<double> FlowPoints { get; } = [];
    public ObservableCollection<double> TempPoints { get; } = [];

    // 状态属性
    [ObservableProperty]
    private string _connectionState = "未连接";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private int _sampleIntervalMs = 1000;

    partial void OnSampleIntervalMsChanged(int value) => OnPropertyChanged(nameof(CurveInfoText));

    /// <summary>趋势曲线标题描述文本</summary>
    public string CurveInfoText => $"采样周期 {SampleIntervalMs}ms · 窗口 {MaxPoints} 点 · 已采 {PressurePoints.Count} 点";

    [ObservableProperty]
    private string _sessionInfo = "未开始监视";

    [ObservableProperty]
    private string _plcIpAddress = "127.0.0.1";

    /// <summary>可编辑的寄存器变量列表（用于 UI 配置）</summary>
    public ObservableCollection<MonitorVariable> MonitorVariables { get; } = [];

    public RealtimeMonitorViewModel()
    {        // 加载 PLC 寄存器配置
        LoadPlcConfig();
        Log.Information("[实时监视] 初始化完成，寄存器数={Count}, IP={IP}", _registerConfigs.Count, PlcIpAddress);

        // 创建 DispatcherTimer（在 UI 线程运行，直接更新图表）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SampleIntervalMs) };
        _timer.Tick += (_, _) =>
        {
            if (!_disposed && _isMonitoring) TickAsync();
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

        // 更新定时器间隔
        _timer.Interval = TimeSpan.FromMilliseconds(SampleIntervalMs);

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
        catch { }

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
    /// 定时器回调：读取 PLC 寄存器并更新 UI
    /// </summary>
    private async Task TickAsync()
    {
        if (_plcConnection == null || _readCts == null) return;

        try
        {
            var cts = _readCts;
            if (cts.IsCancellationRequested) return;

            // 构建读取请求
            var requests = _registerConfigs
                .Select(vc => new PlcRegisterRequest { Address = vc.RegisterAddress, DataType = vc.DataType })
                .ToList();

            if (_tickCount == 0)
                Log.Information("[实时监视] Tick 开始，寄存器数={Count}, 采样间隔={Interval}ms", requests.Count, SampleIntervalMs);

            // 批量读取所有寄存器
            var result = await _plcConnection.ReadMultipleAsync(requests, cts.Token);

            if (!result.IsSuccess || result.Data == null)
            {
                if (_tickCount % 10 == 0)
                    Log.Warning("[实时监视] 读取失败: {Error}", result.Error);
                ConnectionState = $"读取失败：{result.Error}";
                return;
            }

            var data = result.Data;

            if (_tickCount == 0)
                Log.Information("[实时监视] 读取成功，数据点数={Count}", data.Count);

            // 更新 Variables 和 MonitorVariables 和曲线
            foreach (var vc in _registerConfigs)
            {
                var item = Variables.FirstOrDefault(v => v.VariableCode == vc.VariableCode);
                var mv = MonitorVariables.FirstOrDefault(m => m.VariableName == vc.VariableName);

                if (data.TryGetValue(vc.RegisterAddress, out var value))
                {
                    var strVal = vc.DataType == "ushort"
                        ? ((ushort)value).ToString()
                        : value.ToString("F4");
                    var nowStr = DateTime.Now.ToString("HH:mm:ss");

                    if (item != null)
                    {
                        item.CurrentValue = strVal;
                        item.UpdatedAt = nowStr;
                        item.Status = "正常";
                    }
                    if (mv != null)
                    {
                        mv.CurrentValue = strVal;
                        mv.UpdatedAt = nowStr;
                        mv.Status = "正常";
                    }

                    // 添加到曲线通道
                    if (vc.CurveChannel != null)
                    {
                        if (_tickCount < 3)
                            Log.Information("[实时监视] 寄存器{Addr} → 通道{Channel} 值{Value}", vc.RegisterAddress, vc.CurveChannel, strVal);
                        AddToChannel(vc.CurveChannel, value);
                    }
                }
                else
                {
                    if (_tickCount < 3)
                        Log.Warning("[实时监视] 寄存器{Addr} 未返回数据", vc.RegisterAddress);
                    if (item != null) item.Status = "未读取到数据";
                    if (mv != null) mv.Status = "未读取到数据";
                }
            }

            _tickCount++;

            // 更新曲线标题状态（实时采样计数）
            if (_tickCount % 10 == 0)
            {
                OnPropertyChanged(nameof(CurveInfoText));
            }

            // 定期保存曲线数据（带错误处理，不影响实时显示）
            if (_tickCount % SaveInterval == 0 && _currentSessionCode != null && _realtimeDataService != null)
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
                    // 保存失败不影响实时监视，只在状态栏提示
                    ConnectionState = $"读取正常，保存曲线失败：{ex.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (Exception ex)
        {
            ConnectionState = $"读取失败：{ex.Message}";
            await StopMonitoringAsync();
        }
    }

    /// <summary>
    /// 添加数据点到曲线通道
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
        _readCts?.Cancel();
        _readCts?.Dispose();

        // 同步释放 PLC 资源（避免 .Wait() 死锁）
        try { (_plcConnection as IDisposable)?.Dispose(); } catch { }
        _plcConnection = null;
    }
}
