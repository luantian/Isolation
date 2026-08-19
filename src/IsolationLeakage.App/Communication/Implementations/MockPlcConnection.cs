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
    private DateTime _startTime;

    public ConnectionStatus Status => _connected ? ConnectionStatus.Online : ConnectionStatus.Offline;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public MockPlcConnection()
    {
        // 使用不固定种子，每次启动尖峰位置不同
        _rnd = new Random();
        _basePressure = 1.5;   // 基准压力 MPa
        _baseFlow = 0.012;     // 基准泄漏率 Nml/min
        _baseTemp = 24.5;      // 基准温度 °C
        _startTime = DateTime.Now; // 初始值，连接时会重置
    }

    public Task<DeviceResult> ConnectAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        _connected = true;
        _startTime = DateTime.Now; // 连接时重置时间，确保从 0 开始计时
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

        // 调用 GetSimulatedValue 获取带尖峰的模拟值，然后转换为 ushort
        double simulatedValue = GetSimulatedValue(startAddress);
        ushort value = (ushort)Math.Max(0, Math.Min(65535, simulatedValue));
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
    /// 地址对应：512=压力P1, 804=流量M1, 806=流量M2, 500=温度T, 504=压力P2, 508=阀开度P1
    /// </summary>
    private double GetSimulatedValue(int address)
    {
        var t = (DateTime.Now - _startTime).TotalSeconds;
        double noise = (_rnd.NextDouble() - 0.5);

        // 流量脉冲逻辑：前 10 秒低位(~25)，10~20 秒陡增到 18000，20 秒后陡降回低位(~25)
        double flowBase;
        double flowNoise;
        if (t < 10.0)
        {
            flowBase = 25;
            flowNoise = noise * 3;
        }
        else if (t < 20.0)
        {
            flowBase = 18000;
            flowNoise = noise * 500;
        }
        else
        {
            flowBase = 25;
            flowNoise = noise * 3;
        }

        return address switch
        {
            // 压力 P1：基准 1.5 MPa，±0.5 正弦波动（周期约 20s）
            512 => Math.Max(0, 1.5 + 0.5 * Math.Sin(t / 3.2) + noise * 0.1),
            // 压力 P2：基准 1.35 MPa，略滞后于 P1
            504 => Math.Max(0, 1.35 + 0.4 * Math.Sin(t / 3.2 - 0.5) + noise * 0.1),
            // 温度 T：基准 25℃，缓慢上升 + 波动
            500 => 25.0 + 3.0 * Math.Sin(t / 8.0) + noise * 0.3,
            // 流量 M1（ushort 整数）：0~10s 低位(~25)，10~20s 陡增到 18000，20s 后陡降回低位
            804 => Math.Max(0, flowBase + flowNoise),
            // 流量 M2（ushort 整数）：0~10s 低位(~25)，10~20s 陡增到 18000，20s 后陡降回低位
            806 => Math.Max(0, flowBase + flowNoise * 0.8),
            // 阀开度 P1：0~100%，缓慢开/关循环（周期约 60s）
            508 => Math.Clamp(50.0 + 45.0 * Math.Sin(t / 9.5) + noise * 1.5, 0, 100),
            // 其他地址：同流量逻辑
            _ => Math.Max(0, flowBase + flowNoise)
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
