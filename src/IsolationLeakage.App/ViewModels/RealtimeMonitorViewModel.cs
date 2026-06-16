using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Configuration;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 实时监视视图模型
/// </summary>
public sealed partial class RealtimeMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly System.Timers.Timer _timer;
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

    // 曲线数据
    [ObservableProperty]
    private ObservableCollection<double> _pressurePoints = [];

    [ObservableProperty]
    private ObservableCollection<double> _flowPoints = [];

    [ObservableProperty]
    private ObservableCollection<double> _tempPoints = [];

    // 状态属性
    [ObservableProperty]
    private string _connectionState = "未连接";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private int _sampleIntervalMs = 500;

    [ObservableProperty]
    private string _sessionInfo = "未开始监视";

    [ObservableProperty]
    private string _plcIpAddress = "127.0.0.1";

    public RealtimeMonitorViewModel()
    {
        // 加载 PLC 寄存器配置
        LoadPlcConfig();

        // 创建定时器（不启动）
        _timer = new System.Timers.Timer(SampleIntervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) =>
        {
            if (!_disposed && Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!_disposed && _isMonitoring) TickAsync();
                });
            }
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
            SampleIntervalMs = 500;

            // 从配置动态构建 Variables 列表
            Variables.Clear();
            foreach (var vc in _registerConfigs)
            {
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
    [ObservableProperty] private double _pressureMax;
    [ObservableProperty] private double _flowMin;
    [ObservableProperty] private double _flowMax;
    [ObservableProperty] private double _tempMin;
    [ObservableProperty] private double _tempMax;

    // 变量列表
    public ObservableCollection<RealtimeVariableItem> Variables { get; } = [];

    public string BoundaryNote => "通过 Modbus TCP 读取 PLC 实时变量；不在本软件中下发试验任务或执行现场控制。";

    // ========== 命令 ==========

    /// <summary>
    /// 连接 PLC
    /// </summary>
    [RelayCommand]
    private async Task ConnectPlcAsync()
    {
        if (IsConnected) return;

        try
        {
            ConnectionState = "正在连接...";
            _plcConnection = AppServices.ModbusPlcConnectionFactory.Create();

            var result = await _plcConnection.ConnectAsync(
                _plcConnectionConfig.IpAddress,
                _plcConnectionConfig.Port);

            if (result.IsSuccess)
            {
                IsConnected = true;
                ConnectionState = $"已连接 {_plcConnectionConfig.IpAddress}:{_plcConnectionConfig.Port}";
            }
            else
            {
                ConnectionState = $"连接失败：{result.Error}";
                _plcConnection = null;
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionState = $"连接异常：{ex.Message}";
            _plcConnection = null;
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
    private async void TickAsync()
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

            // 批量读取所有寄存器
            var result = await _plcConnection.ReadMultipleAsync(requests, cts.Token);

            if (!result.IsSuccess || result.Data == null)
            {
                ConnectionState = $"读取失败：{result.Error}";
                return;
            }

            var data = result.Data;

            // 更新 Variables 和曲线
            foreach (var vc in _registerConfigs)
            {
                var item = Variables.FirstOrDefault(v => v.VariableCode == vc.VariableCode);
                if (item == null) continue;

                if (data.TryGetValue(vc.RegisterAddress, out var value))
                {
                    item.CurrentValue = vc.DataType == "ushort"
                        ? ((ushort)value).ToString()
                        : value.ToString("F4");
                    item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss.fff");
                    item.Status = "正常";

                    // 添加到曲线通道
                    if (vc.CurveChannel != null)
                    {
                        AddToChannel(vc.CurveChannel, value);
                    }
                }
                else
                {
                    item.Status = "未读取到数据";
                }
            }

            _tickCount++;

            // 定期保存曲线数据
            if (_tickCount % SaveInterval == 0 && _currentSessionCode != null && _realtimeDataService != null)
            {
                _ = _realtimeDataService.SaveCurveAsync(
                    _currentSessionCode,
                    PressurePoints.ToArray(),
                    FlowPoints.ToArray(),
                    TempPoints.ToArray(),
                    PressurePoints.Count);
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
        var collection = channel switch
        {
            "Pressure" => PressurePoints,
            "Flow" => FlowPoints,
            "Temp" => TempPoints,
            _ => null
        };

        if (collection != null)
        {
            collection.Add(value);
            if (collection.Count > MaxPoints) collection.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _readCts?.Cancel();
        _readCts?.Dispose();
        _ = DisconnectPlcAsync();
    }
}
