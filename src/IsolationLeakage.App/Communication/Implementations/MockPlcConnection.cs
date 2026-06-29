using System.Collections.Concurrent;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// 模拟 PLC 连接（用于开发/测试阶段，无需真实硬件即可演示实时监视功能）
///
/// 工作模式：软件轮询
/// - 软件定时发读取请求 → 模拟 PLC 返回一组带噪声的仿真数据
/// - 断开/停止时不再返回数据
/// </summary>
public sealed class MockPlcConnection : IModbusPlcConnection
{
    private bool _connected;
    private bool _disposed;
    private readonly Random _rnd;
    private readonly double _basePressure;
    private readonly double _baseFlow;
    private readonly double _baseTemp;
    private readonly DateTime _startTime;

    public ConnectionStatus Status => _connected ? ConnectionStatus.Online : ConnectionStatus.Offline;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public MockPlcConnection()
    {
        _rnd = new Random(42);
        _basePressure = 1.5;   // 基准压力 MPa
        _baseFlow = 0.012;     // 基准泄漏率 L/min
        _baseTemp = 24.5;      // 基准温度 °C
        _startTime = DateTime.Now;
    }

    public Task<DeviceResult> ConnectAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        _connected = true;
        OnStateChanged(ConnectionStatus.Offline, ConnectionStatus.Online, $"[模拟] 已连接 PLC {ipAddress}:{port}");
        return Task.FromResult(DeviceResult.Success($"[模拟] 已连接 PLC {ipAddress}:{port}"));
    }

    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        OnStateChanged(ConnectionStatus.Online, ConnectionStatus.Offline, "[模拟] 已断开 PLC");
        return Task.FromResult(DeviceResult.Success("[模拟] 已断开连接"));
    }

    public Task<DeviceResult<double>> ReadDoubleAsync(int startAddress, CancellationToken ct = default)
    {
        if (!_connected) return Task.FromResult(DeviceResult<double>.Fail("PLC 未连接"));

        double value = GetSimulatedValue(startAddress);
        return Task.FromResult(DeviceResult<double>.Success(value));
    }

    public Task<DeviceResult<ushort>> ReadUshortAsync(int startAddress, CancellationToken ct = default)
    {
        if (!_connected) return Task.FromResult(DeviceResult<ushort>.Fail("PLC 未连接"));

        ushort value = (ushort)(_rnd.Next(0, 65535));
        return Task.FromResult(DeviceResult<ushort>.Success(value));
    }

    public Task<DeviceResult<Dictionary<int, double>>> ReadMultipleAsync(
        IReadOnlyList<PlcRegisterRequest> requests, CancellationToken ct = default)
    {
        if (!_connected) return Task.FromResult(DeviceResult<Dictionary<int, double>>.Fail("PLC 未连接"));

        var result = new Dictionary<int, double>();
        foreach (var req in requests)
        {
            result[req.Address] = GetSimulatedValue(req.Address);
        }

        return Task.FromResult(DeviceResult<Dictionary<int, double>>.Success(result));
    }

    /// <summary>
    /// 根据真实装置寄存器地址返回带明显波动的仿真值（正弦波 + 噪声），便于演示曲线。
    /// 地址对应：512=压力P1, 804=流量M1, 806=流量M2, 500=温度T, 504=压力P2
    /// </summary>
    private double GetSimulatedValue(int address)
    {
        var t = (DateTime.Now - _startTime).TotalSeconds;
        double noise = (_rnd.NextDouble() - 0.5);

        return address switch
        {
            // 压力 P1：基准 1.5 MPa，±0.3 正弦波动（周期约 20s）
            512 => Math.Max(0, 1.5 + 0.3 * Math.Sin(t / 3.2) + noise * 0.03),
            // 压力 P2：基准 1.35 MPa，略滞后于 P1
            504 => Math.Max(0, 1.35 + 0.25 * Math.Sin(t / 3.2 - 0.5) + noise * 0.03),
            // 温度 T：基准 25℃，缓慢上升 + 小波动
            500 => 25.0 + 2.0 * Math.Sin(t / 8.0) + noise * 0.1,
            // 流量 M1（ushort 整数）：基准 20，±8 波动
            804 => Math.Max(0, 20 + 8 * Math.Sin(t / 2.5) + noise * 1.5),
            // 流量 M2（ushort 整数）：基准 18，相位不同
            806 => Math.Max(0, 18 + 6 * Math.Cos(t / 2.8) + noise * 1.2),
            // 其他地址：基准值 + 噪声
            _ => 1.0 + noise * 0.05
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_connected)
            {
                _connected = false;
                OnStateChanged(ConnectionStatus.Online, ConnectionStatus.Offline, "[模拟] 已断开 PLC");
            }
        }
    }

    private void OnStateChanged(ConnectionStatus oldStatus, ConnectionStatus newStatus, string message)
    {
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Message = message
        });
    }
}
