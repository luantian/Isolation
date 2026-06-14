using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Interfaces;

/// <summary>
/// 设备通讯接口（USB、RJ45、RS232、RS485 通用抽象）
/// </summary>
public interface IDeviceConnection : IDisposable
{
    /// <summary>通讯方式</summary>
    CommunicationType TransportType { get; }

    /// <summary>当前连接状态</summary>
    ConnectionStatus Status { get; }

    /// <summary>连接状态变化事件</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>建立连接</summary>
    Task<DeviceResult> ConnectAsync(ConnectionConfig config, CancellationToken ct = default);

    /// <summary>断开连接</summary>
    Task<DeviceResult> DisconnectAsync(CancellationToken ct = default);

    /// <summary>下发试验任务至测量装置</summary>
    Task<DeviceResult<SendTaskResult>> SendTaskAsync(TaskPayload payload, CancellationToken ct = default);

    /// <summary>从测量装置接收试验数据</summary>
    Task<DeviceResult<DataPayload>> ReceiveDataAsync(CancellationToken ct = default);

    /// <summary>检查设备状态</summary>
    Task<DeviceStatus> CheckStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// 连接状态变化事件参数
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStatus OldStatus { get; init; }
    public ConnectionStatus NewStatus { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 设备状态信息
/// </summary>
public sealed class DeviceStatus
{
    public bool IsOnline { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public DateTime? LastHeartbeat { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// 任务下发结果
/// </summary>
public sealed class SendTaskResult
{
    public int TotalObjects { get; init; }
    public int SentCount { get; init; }
    public int FailedCount { get; init; }
    public List<string> FailedObjects { get; init; } = [];
    public string Detail { get; init; } = string.Empty;
}
