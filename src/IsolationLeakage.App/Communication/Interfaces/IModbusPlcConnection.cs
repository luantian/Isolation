using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Interfaces;

/// <summary>
/// PLC Modbus 通讯接口（用于实时监视）
/// </summary>
public interface IModbusPlcConnection : IDisposable
{
    /// <summary>当前连接状态</summary>
    ConnectionStatus Status { get; }

    /// <summary>连接状态变化事件</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>建立到 PLC 的连接</summary>
    Task<DeviceResult> ConnectAsync(string ipAddress, int port, CancellationToken ct = default);

    /// <summary>断开与 PLC 的连接</summary>
    Task<DeviceResult> DisconnectAsync(CancellationToken ct = default);

    /// <summary>从指定寄存器地址读取一个 double 值（占 2 个寄存器）</summary>
    Task<DeviceResult<double>> ReadDoubleAsync(int startAddress, CancellationToken ct = default);

    /// <summary>从指定寄存器地址读取一个 ushort 值</summary>
    Task<DeviceResult<ushort>> ReadUshortAsync(int startAddress, CancellationToken ct = default);

    /// <summary>批量读取多个寄存器</summary>
    Task<DeviceResult<Dictionary<int, double>>> ReadMultipleAsync(IReadOnlyList<PlcRegisterRequest> requests, CancellationToken ct = default);
}

/// <summary>
/// PLC 寄存器读取请求
/// </summary>
public sealed class PlcRegisterRequest
{
    /// <summary>寄存器地址</summary>
    public int Address { get; init; }

    /// <summary>数据类型：double(2 regs) 或 ushort(1 reg)</summary>
    public string DataType { get; init; } = "double";

    public override string ToString() => $"{Address}({DataType})";
}
