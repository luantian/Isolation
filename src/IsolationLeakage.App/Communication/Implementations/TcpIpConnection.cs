using System.Net.Http;
using System.Net.Http.Json;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// TCP/IP 网络连接（REST API 方式）
/// 通过 HTTP 与测量装置通讯。设备 API 地址和端口需根据实际硬件确认。
/// </summary>
public sealed class TcpIpConnection : IDeviceConnection
{
    private TcpConfig? _config;
    private HttpClient? _httpClient;
    private string? _baseUrl;
    private bool _disposed;

    public CommunicationType TransportType => CommunicationType.Rj45;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Unknown;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 建立 HTTP 连接到设备
    /// </summary>
    public Task<DeviceResult> ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        if (config is not TcpConfig tcpConfig)
            return Task.FromResult(DeviceResult.Fail("配置类型不匹配，需要 TcpConfig"));

        if (string.IsNullOrWhiteSpace(tcpConfig.IpAddress))
            return Task.FromResult(DeviceResult.Fail("IP 地址不能为空"));

        _config = tcpConfig;
        _baseUrl = $"http://{tcpConfig.IpAddress}:{tcpConfig.Port}{tcpConfig.BasePath}";
        _httpClient = new HttpClient { Timeout = tcpConfig.Timeout };

        var oldStatus = Status;
        Status = ConnectionStatus.Online;
        OnStateChanged(oldStatus, Status, $"已连接 {_baseUrl}");

        return Task.FromResult(DeviceResult.Success($"已连接设备 {_baseUrl}"));
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (Status == ConnectionStatus.Unknown)
            return Task.FromResult(DeviceResult.Fail("未连接"));

        _httpClient?.Dispose();
        _httpClient = null;
        _baseUrl = null;

        var oldStatus = Status;
        Status = ConnectionStatus.Offline;
        OnStateChanged(oldStatus, Status, "已断开网络连接");
        return Task.FromResult(DeviceResult.Success());
    }

    /// <summary>
    /// 通过 REST API 下发试验任务
    /// 实际端点和请求格式需根据设备 API 文档确认
    /// </summary>
    public async Task<DeviceResult<SendTaskResult>> SendTaskAsync(TaskPayload payload, CancellationToken ct = default)
    {
        if (_httpClient == null || _baseUrl == null)
            return DeviceResult<SendTaskResult>.Fail("未连接设备");

        try
        {
            var endpoint = $"{_baseUrl}/tasks";
            var response = await _httpClient.PostAsJsonAsync(endpoint, payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                return DeviceResult<SendTaskResult>.Fail(
                    $"HTTP {response.StatusCode}：下发任务失败");
            }

            var result = new SendTaskResult
            {
                TotalObjects = payload.Objects.Count,
                SentCount = payload.Objects.Count,
                FailedCount = 0,
                Detail = $"任务已通过 API 下发至 {endpoint}"
            };

            return DeviceResult<SendTaskResult>.Success(result);
        }
        catch (HttpRequestException ex)
        {
            return DeviceResult<SendTaskResult>.Fail($"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return DeviceResult<SendTaskResult>.Fail($"下发任务失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从设备 REST API 接收试验数据
    /// 实际端点和响应格式需根据设备 API 文档确认
    /// </summary>
    public async Task<DeviceResult<DataPayload>> ReceiveDataAsync(CancellationToken ct = default)
    {
        if (_httpClient == null || _baseUrl == null)
            return DeviceResult<DataPayload>.Fail("未连接设备");

        try
        {
            var endpoint = $"{_baseUrl}/results/latest";
            var payload = await _httpClient.GetFromJsonAsync<DataPayload>(endpoint, ct);

            if (payload == null)
            {
                return DeviceResult<DataPayload>.Fail("设备返回空数据");
            }

            return DeviceResult<DataPayload>.Success(payload);
        }
        catch (HttpRequestException ex)
        {
            return DeviceResult<DataPayload>.Fail($"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return DeviceResult<DataPayload>.Fail($"接收数据失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 通过 HTTP 健康检查端点确认设备状态
    /// </summary>
    public async Task<DeviceStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        if (_httpClient == null || _baseUrl == null)
        {
            return new DeviceStatus { IsOnline = false, Detail = "未连接" };
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health", ct);
            return new DeviceStatus
            {
                IsOnline = response.IsSuccessStatusCode,
                DeviceId = _config?.IpAddress ?? string.Empty,
                FirmwareVersion = string.Empty,
                LastHeartbeat = DateTime.Now,
                Detail = $"HTTP {(int)response.StatusCode}"
            };
        }
        catch
        {
            return new DeviceStatus
            {
                IsOnline = false,
                DeviceId = _config?.IpAddress ?? string.Empty,
                Detail = "健康检查失败"
            };
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
