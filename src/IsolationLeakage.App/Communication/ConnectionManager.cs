using System.Timers;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Communication;

/// <summary>
/// 连接管理器
/// 跟踪每个设备的活跃连接，定期心跳检查并同步状态到数据库。
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly IConnectionFactory _factory;
    private readonly Dictionary<string, IDeviceConnection> _connections = new();
    private readonly Dictionary<string, ConnectionConfig> _configs = new();
    private readonly System.Timers.Timer? _heartbeatTimer;
    private bool _disposed;

    /// <summary>
    /// 心跳间隔（秒）
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public ConnectionManager(IConnectionFactory factory)
    {
        _factory = factory;
        _heartbeatTimer = new System.Timers.Timer(HeartbeatIntervalSeconds * 1000)
        {
            AutoReset = true,
            Enabled = false
        };
        _heartbeatTimer.Elapsed += HeartbeatTick;
    }

    /// <summary>
    /// 获取设备当前连接实例
    /// </summary>
    public IDeviceConnection? GetConnection(string deviceCode)
    {
        lock (_connections)
        {
            return _connections.GetValueOrDefault(deviceCode);
        }
    }

    /// <summary>
    /// 获取所有已连接设备的编码列表
    /// </summary>
    public IReadOnlyList<string> GetConnectedDevices()
    {
        lock (_connections)
        {
            return _connections.Keys.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 连接设备
    /// </summary>
    /// <param name="deviceCode">测量装置编码</param>
    /// <param name="config">连接配置</param>
    public async Task<Results.DeviceResult> ConnectDeviceAsync(string deviceCode, ConnectionConfig config)
    {
        if (_disposed)
            return Results.DeviceResult.Fail("连接管理器已释放");

        lock (_connections)
        {
            if (_connections.ContainsKey(deviceCode))
                return Results.DeviceResult.Fail($"设备 {deviceCode} 已连接，请先断开");
        }

        var connection = _factory.Create(config.TransportType);
        var result = await connection.ConnectAsync(config);

        if (result.IsSuccess)
        {
            lock (_connections)
            {
                _connections[deviceCode] = connection;
                _configs[deviceCode] = config;
            }

            await UpdateDeviceStatusInDbAsync(deviceCode, ConnectionStatus.Online);
        }
        else
        {
            connection.Dispose();
        }

        return result;
    }

    /// <summary>
    /// 断开设备连接
    /// </summary>
    public async Task<Results.DeviceResult> DisconnectDeviceAsync(string deviceCode)
    {
        IDeviceConnection? connection;
        lock (_connections)
        {
            if (!_connections.TryGetValue(deviceCode, out connection))
                return Results.DeviceResult.Fail($"设备 {deviceCode} 未连接");

            _connections.Remove(deviceCode);
            _configs.Remove(deviceCode);
        }

        var result = await connection.DisconnectAsync();
        connection.Dispose();

        if (result.IsSuccess)
        {
            await UpdateDeviceStatusInDbAsync(deviceCode, ConnectionStatus.Offline);
        }

        return result;
    }

    /// <summary>
    /// 启动心跳检查（定期查询设备状态并同步到数据库）
    /// </summary>
    public void StartHeartbeat()
    {
        _heartbeatTimer?.Start();
    }

    /// <summary>
    /// 停止心跳检查
    /// </summary>
    public void StopHeartbeat()
    {
        _heartbeatTimer?.Stop();
    }

    /// <summary>
    /// 断开所有连接并释放资源
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        List<(string code, IDeviceConnection conn)> toDispose;
        lock (_connections)
        {
            toDispose = _connections.ToList().Select(kvp => (kvp.Key, kvp.Value)).ToList();
            _connections.Clear();
            _configs.Clear();
        }

        foreach (var (code, conn) in toDispose)
        {
            try
            {
                await conn.DisconnectAsync();
                await UpdateDeviceStatusInDbAsync(code, ConnectionStatus.Offline);
            }
            catch { }
            finally { conn.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _heartbeatTimer?.Stop();
            _heartbeatTimer?.Dispose();

            // 同步断开所有连接（避免 .Wait() 死锁）
            List<IDeviceConnection> toDispose;
            lock (_connections)
            {
                toDispose = _connections.Values.ToList();
                _connections.Clear();
                _configs.Clear();
            }
            foreach (var conn in toDispose)
            {
                try { conn.Dispose(); } catch { }
            }
        }
    }

    private async void HeartbeatTick(object? sender, ElapsedEventArgs e)
    {
        List<(string code, IDeviceConnection conn)> connections;
        lock (_connections)
        {
            connections = _connections.ToList().Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        foreach (var (code, conn) in connections)
        {
            try
            {
                var status = await conn.CheckStatusAsync();
                var newStatus = status.IsOnline ? ConnectionStatus.Online : ConnectionStatus.Offline;

                if (conn.Status != newStatus)
                {
                    await UpdateDeviceStatusInDbAsync(code, newStatus);
                }
            }
            catch
            {
                // 心跳检查失败不中断其他设备检查
            }
        }
    }

    private static async Task UpdateDeviceStatusInDbAsync(string deviceCode, ConnectionStatus status)
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var device = await context.MeasurementDevices.FindAsync(deviceCode);
            if (device != null)
            {
                device.ConnectionStatus = status;
                device.LastSyncTime = DateTime.Now;
                await context.SaveChangesAsync();
            }
        }
        catch
        {
            // 心跳中不抛异常影响主流程
        }
    }
}
