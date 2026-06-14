using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// RS232 / RS485 串口连接
/// 使用 System.IO.Ports.SerialPort 与设备进行串口通讯。
/// 具体帧格式（帧头、帧尾、JSON 载荷等）需与设备端约定。
/// </summary>
public sealed class SerialConnection : IDeviceConnection
{
    private SerialConfig? _config;
    private System.IO.Ports.SerialPort? _serialPort;
    private bool _disposed;

    public CommunicationType TransportType => CommunicationType.Rs232;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Unknown;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 打开串口连接
    /// </summary>
    public Task<DeviceResult> ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        if (config is not SerialConfig serialConfig)
            return Task.FromResult(DeviceResult.Fail("配置类型不匹配，需要 SerialConfig"));

        // 检查可用串口列表
        var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
        if (Array.IndexOf(availablePorts, serialConfig.PortName) < 0)
        {
            Status = ConnectionStatus.Offline;
            return Task.FromResult(DeviceResult.Fail(
                $"串口 {serialConfig.PortName} 不存在。可用端口：{string.Join(", ", availablePorts)}"));
        }

        try
        {
            _config = serialConfig;
            _serialPort = new System.IO.Ports.SerialPort(serialConfig.PortName)
            {
                BaudRate = serialConfig.BaudRate,
                DataBits = serialConfig.DataBits,
                Parity = serialConfig.Parity,
                StopBits = serialConfig.StopBits,
                ReadTimeout = (int)serialConfig.Timeout.TotalMilliseconds,
                WriteTimeout = (int)serialConfig.Timeout.TotalMilliseconds,
            };

            _serialPort.Open();

            var oldStatus = Status;
            Status = ConnectionStatus.Online;
            OnStateChanged(oldStatus, Status, $"已打开串口 {serialConfig.PortName} @ {serialConfig.BaudRate}bps");

            return Task.FromResult(DeviceResult.Success($"已打开串口 {serialConfig.PortName}"));
        }
        catch (Exception ex)
        {
            _serialPort?.Dispose();
            _serialPort = null;
            Status = ConnectionStatus.Offline;
            return Task.FromResult(DeviceResult.Fail($"打开串口失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 关闭串口连接
    /// </summary>
    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return Task.FromResult(DeviceResult.Fail("串口未打开"));

        try
        {
            _serialPort.Close();
            _serialPort.Dispose();
            _serialPort = null;
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult.Fail($"关闭串口失败：{ex.Message}"));
        }

        var oldStatus = Status;
        Status = ConnectionStatus.Offline;
        OnStateChanged(oldStatus, Status, "已关闭串口");
        return Task.FromResult(DeviceResult.Success());
    }

    /// <summary>
    /// 通过串口下发试验任务
    /// 注意：帧格式为占位值，实际需与设备约定。
    /// 当前实现：发送 JSON 帧 \x02 + JSON + \x03
    /// </summary>
    public Task<DeviceResult<SendTaskResult>> SendTaskAsync(TaskPayload payload, CancellationToken ct = default)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return Task.FromResult(DeviceResult<SendTaskResult>.Fail("串口未打开"));

        // TODO: 确认帧格式后完善实现
        // 当前占位实现：发送 JSON 字符串
        try
        {
            var json = payload.ToJson();
            // 帧格式：STX(0x02) + JSON + ETX(0x03)
            var frame = new byte[] { 0x02 }
                .Concat(System.Text.Encoding.UTF8.GetBytes(json))
                .Concat(new byte[] { 0x03 })
                .ToArray();

            _serialPort.Write(frame, 0, frame.Length);

            var result = new SendTaskResult
            {
                TotalObjects = payload.Objects.Count,
                SentCount = payload.Objects.Count,
                FailedCount = 0,
                Detail = "任务数据已通过串口发送（帧格式待确认）"
            };

            return Task.FromResult(DeviceResult<SendTaskResult>.Success(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult<SendTaskResult>.Fail($"串口发送失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 从串口接收试验数据
    /// 注意：帧格式为占位值，实际需与设备约定。
    /// </summary>
    public Task<DeviceResult<DataPayload>> ReceiveDataAsync(CancellationToken ct = default)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return Task.FromResult(DeviceResult<DataPayload>.Fail("串口未打开"));

        // TODO: 确认帧格式后完善实现
        // 当前占位实现
        try
        {
            if (_serialPort.BytesToRead == 0)
            {
                return Task.FromResult(DeviceResult<DataPayload>.Fail("串口无数据可读"));
            }

            // 读取帧：等待 STX(0x02)，然后读数据直到 ETX(0x03)
            // 简化实现：读一行
            var line = _serialPort.ReadLine();
            var payload = DataPayload.FromJson(line);

            if (payload == null)
            {
                return Task.FromResult(DeviceResult<DataPayload>.Fail("串口数据解析失败（帧格式待确认）"));
            }

            return Task.FromResult(DeviceResult<DataPayload>.Success(payload));
        }
        catch (TimeoutException)
        {
            return Task.FromResult(DeviceResult<DataPayload>.Fail("串口读取超时"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult<DataPayload>.Fail($"串口读取失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 检查串口状态
    /// </summary>
    public Task<DeviceStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        bool isOpen = _serialPort?.IsOpen == true;
        return Task.FromResult(new DeviceStatus
        {
            IsOnline = isOpen,
            DeviceId = _config?.PortName ?? string.Empty,
            FirmwareVersion = string.Empty,
            LastHeartbeat = isOpen ? DateTime.Now : null,
            Detail = isOpen ? $"{_config!.PortName} @ {_config.BaudRate}bps" : "未连接"
        });
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
