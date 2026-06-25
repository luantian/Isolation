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
            double value = GetSimulatedValue(req.Address);
            if (req.DataType == "ushort")
            {
                value = _rnd.Next(0, 1000);
            }
            result[req.Address] = value;
        }

        return Task.FromResult(DeviceResult<Dictionary<int, double>>.Success(result));
    }

    /// <summary>根据寄存器地址返回对应的仿真值（带缓慢漂移 + 噪声）</summary>
    private double GetSimulatedValue(int address)
    {
        var elapsed = (DateTime.Now - _startTime).TotalSeconds;

        // 模拟缓慢漂移：30秒周期的缓慢波动
        double drift = Math.Sin(elapsed / 30.0) * 0.02;
        double noise = (_rnd.NextDouble() - 0.5) * 0.004;

        return address switch
        {
            // 压力通道
            0 or 1 => _basePressure + drift * 0.1 + noise,

            // 流量通道
            2 or 3 => _baseFlow + drift * 0.001 + noise * 0.1,

            // 试验状态 / 报警码
            4 or 5 => 1.0 + noise,

            // 温度通道
            6 or 7 => _baseTemp + drift * 0.3 + noise * 0.2,

            // 其他地址返回基准值 + 噪声
            _ => 1.0 + noise
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
