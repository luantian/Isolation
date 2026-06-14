using System.Net.Sockets;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;
using NModbus;
using NModbus.Device;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// Modbus PLC 连接（用于实时监视）
/// 支持 Modbus TCP 和 Modbus RTU（串口）两种传输模式。
/// 寄存器地址表需根据实际 PLC 通讯协议确认，当前地址均为占位值。
/// </summary>
public sealed class ModbusPlcConnection : IModbusPlcConnection
{
    private readonly string _protocol; // "tcp" or "rtu"
    private IModbusMaster? _modbusMaster;
    private TcpClient? _tcpClient;
    private System.IO.Ports.SerialPort? _serialPort;
    private bool _disposed;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Unknown;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 创建 Modbus PLC 连接
    /// </summary>
    /// <param name="protocol">传输模式："tcp" 或 "rtu"</param>
    public ModbusPlcConnection(string protocol = "tcp")
    {
        _protocol = protocol;
    }

    /// <summary>
    /// 连接到 PLC
    /// </summary>
    /// <param name="ipAddress">TCP 模式：PLC IP 地址；RTU 模式：串口名称（如 COM3）</param>
    /// <param name="port">TCP 模式：端口号（默认 502）；RTU 模式：波特率（默认 9600）</param>
    public Task<DeviceResult> ConnectAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        try
        {
            if (_protocol == "tcp")
            {
                _tcpClient = new TcpClient();
                _tcpClient.ConnectAsync(ipAddress, port).Wait(5000);

                if (!_tcpClient.Connected)
                {
                    Status = ConnectionStatus.Offline;
                    return Task.FromResult(DeviceResult.Fail($"无法连接到 PLC {ipAddress}:{port}"));
                }

                var factory = new ModbusFactory();
                _modbusMaster = factory.CreateMaster(_tcpClient);
            }
            else
            {
                // RTU 模式：需要额外封装（等待设备协议确认后实现）
                Status = ConnectionStatus.Offline;
                return Task.FromResult(DeviceResult.Fail("Modbus RTU 模式需要设备协议确认后实现，请使用 TCP 模式"));
            }

            var oldStatus = Status;
            Status = ConnectionStatus.Online;
            OnStateChanged(oldStatus, Status, $"已连接 PLC ({_protocol}) {ipAddress}:{port}");

            return Task.FromResult(DeviceResult.Success($"已连接 PLC {_protocol}://{ipAddress}:{port}"));
        }
        catch (Exception ex)
        {
            CleanupResources();
            Status = ConnectionStatus.Offline;
            return Task.FromResult(DeviceResult.Fail($"连接 PLC 失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 断开与 PLC 的连接
    /// </summary>
    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (Status == ConnectionStatus.Unknown)
            return Task.FromResult(DeviceResult.Fail("未连接"));

        CleanupResources();

        var oldStatus = Status;
        Status = ConnectionStatus.Offline;
        OnStateChanged(oldStatus, Status, "已断开 PLC 连接");
        return Task.FromResult(DeviceResult.Success());
    }

    /// <summary>
    /// 从指定寄存器地址读取一个 double 值（占 2 个保持寄存器，IEEE 754 格式）
    /// </summary>
    /// <param name="startAddress">起始寄存器地址</param>
    public async Task<DeviceResult<double>> ReadDoubleAsync(int startAddress, CancellationToken ct = default)
    {
        if (_modbusMaster == null)
            return DeviceResult<double>.Fail("Modbus 未连接");

        try
        {
            // 读 2 个寄存器（每个 16 位），组合为 32 位 float，转为 double
            ushort[] registers = await _modbusMaster.ReadHoldingRegistersAsync(0, (ushort)startAddress, 2);

            // IEEE 754 单精度 float：2 个寄存器 = 32 位
            byte[] bytes = new byte[4];
            bytes[0] = (byte)(registers[0] & 0xFF);
            bytes[1] = (byte)((registers[0] >> 8) & 0xFF);
            bytes[2] = (byte)(registers[1] & 0xFF);
            bytes[3] = (byte)((registers[1] >> 8) & 0xFF);

            float value = BitConverter.ToSingle(bytes, 0);
            return DeviceResult<double>.Success((double)value);
        }
        catch (Exception ex)
        {
            return DeviceResult<double>.Fail($"读取寄存器失败（地址 {startAddress}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 从指定寄存器地址读取一个 ushort 值
    /// </summary>
    public async Task<DeviceResult<ushort>> ReadUshortAsync(int startAddress, CancellationToken ct = default)
    {
        if (_modbusMaster == null)
            return DeviceResult<ushort>.Fail("Modbus 未连接");

        try
        {
            ushort[] registers = await _modbusMaster.ReadHoldingRegistersAsync(0, (ushort)startAddress, 1);
            return DeviceResult<ushort>.Success(registers[0]);
        }
        catch (Exception ex)
        {
            return DeviceResult<ushort>.Fail($"读取寄存器失败（地址 {startAddress}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 批量读取多个寄存器地址
    /// </summary>
    public async Task<DeviceResult<Dictionary<int, double>>> ReadMultipleAsync(
        IReadOnlyList<PlcRegisterRequest> requests, CancellationToken ct = default)
    {
        if (_modbusMaster == null)
            return DeviceResult<Dictionary<int, double>>.Fail("Modbus 未连接");

        var result = new Dictionary<int, double>();

        try
        {
            foreach (var req in requests)
            {
                try
                {
                    if (req.DataType == "ushort")
                    {
                        ushort[] registers = await _modbusMaster.ReadHoldingRegistersAsync(0, (ushort)req.Address, 1);
                        result[req.Address] = registers[0];
                    }
                    else
                    {
                        ushort[] registers = await _modbusMaster.ReadHoldingRegistersAsync(0, (ushort)req.Address, 2);
                        byte[] bytes = new byte[4];
                        bytes[0] = (byte)(registers[0] & 0xFF);
                        bytes[1] = (byte)((registers[0] >> 8) & 0xFF);
                        bytes[2] = (byte)(registers[1] & 0xFF);
                        bytes[3] = (byte)((registers[1] >> 8) & 0xFF);
                        float value = BitConverter.ToSingle(bytes, 0);
                        result[req.Address] = value;
                    }
                }
                catch
                {
                    result[req.Address] = double.NaN; // 标记为无效
                }
            }

            return DeviceResult<Dictionary<int, double>>.Success(result);
        }
        catch (Exception ex)
        {
            return DeviceResult<Dictionary<int, double>>.Fail($"批量读取失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (Status == ConnectionStatus.Online)
                DisconnectAsync().Wait();
        }
    }

    private void CleanupResources()
    {
        try { _modbusMaster?.Dispose(); } catch { }
        try { _tcpClient?.Dispose(); } catch { }
        try { _serialPort?.Dispose(); } catch { }
        _modbusMaster = null;
        _tcpClient = null;
        _serialPort = null;
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
