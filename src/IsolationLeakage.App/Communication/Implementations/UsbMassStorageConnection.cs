using System.IO;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;
using Serilog;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// USB 大容量存储连接（U 盘文件读写方式）
/// 测量装置将试验结果保存为 JSON 文件到 U 盘，软件读取并归档。
/// 任务文件也通过 U 盘传递给装置。
/// </summary>
public sealed class UsbMassStorageConnection : IDeviceConnection
{
    private UsbConfig? _config;
    private string? _drivePath;
    private bool _disposed;

    public CommunicationType TransportType => CommunicationType.Usb;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Unknown;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 连接至 U 盘（验证盘符是否存在并创建必要目录）
    /// </summary>
    public Task<DeviceResult> ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(DeviceResult.Fail("连接已释放"));

        if (config is not UsbConfig usbConfig)
            return Task.FromResult(DeviceResult.Fail("配置类型不匹配，需要 UsbConfig"));

        var driveLetter = usbConfig.DriveLetter.TrimEnd(':').TrimEnd('\\');
        _drivePath = $"{driveLetter}:\\";

        if (!Directory.Exists(_drivePath))
        {
            Status = ConnectionStatus.Offline;
            return Task.FromResult(DeviceResult.Fail($"U 盘盘符 {driveLetter}:\\ 不存在，请确认已插入"));
        }

        var driveInfo = new DriveInfo(driveLetter);
        if (driveInfo.DriveType != DriveType.Removable && driveInfo.DriveType != DriveType.Fixed)
        {
            Status = ConnectionStatus.Offline;
            return Task.FromResult(DeviceResult.Fail($"{driveLetter}:\\ 不是可移动磁盘"));
        }

        // 创建必要目录
        var taskDir = Path.Combine(_drivePath, usbConfig.TaskFolder);
        var resultDir = Path.Combine(_drivePath, usbConfig.ResultFolder);
        var archiveDir = Path.Combine(_drivePath, usbConfig.ArchiveFolder);

        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(resultDir);
        Directory.CreateDirectory(archiveDir);

        _config = usbConfig;
        var oldStatus = Status;
        Status = ConnectionStatus.Online;
        OnStateChanged(oldStatus, Status, $"已连接 U 盘 {driveLetter}:\\");

        return Task.FromResult(DeviceResult.Success($"已连接 U 盘 {driveLetter}:\\"));
    }

    /// <summary>
    /// 断开连接（仅标记状态，不弹出 U 盘）
    /// </summary>
    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (Status == ConnectionStatus.Unknown)
            return Task.FromResult(DeviceResult.Fail("未连接"));

        var oldStatus = Status;
        Status = ConnectionStatus.Offline;
        OnStateChanged(oldStatus, Status, "已断开 U 盘连接");
        return Task.FromResult(DeviceResult.Success());
    }

    /// <summary>
    /// 下发试验任务：将 TaskPayload 序列化为 JSON 写入 U 盘 tasks/ 目录
    /// </summary>
    public Task<DeviceResult<SendTaskResult>> SendTaskAsync(TaskPayload payload, CancellationToken ct = default)
    {
        if (_config == null || _drivePath == null)
            return Task.FromResult(DeviceResult<SendTaskResult>.Fail("未连接 U 盘"));

        try
        {
            var taskDir = Path.Combine(_drivePath, _config.TaskFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"task_{payload.TaskId}_{timestamp}.json";
            var filePath = Path.Combine(taskDir, fileName);

            var json = payload.ToJson();
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);

            var result = new SendTaskResult
            {
                TotalObjects = payload.Objects.Count,
                SentCount = payload.Objects.Count,
                FailedCount = 0,
                Detail = $"任务文件已写入：{filePath}"
            };

            return Task.FromResult(DeviceResult<SendTaskResult>.Success(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult<SendTaskResult>.Fail($"写入任务文件失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 从 U 盘接收试验数据：扫描 results/ 目录下的 JSON 文件，解析为 DataPayload
    /// 解析成功后将文件移动到 archive/ 目录（只归档不删除）
    /// </summary>
    public Task<DeviceResult<DataPayload>> ReceiveDataAsync(CancellationToken ct = default)
    {
        if (_config == null || _drivePath == null)
            return Task.FromResult(DeviceResult<DataPayload>.Fail("未连接 U 盘"));

        try
        {
            var resultDir = Path.Combine(_drivePath, _config.ResultFolder);
            var archiveDir = Path.Combine(_drivePath, _config.ArchiveFolder);

            var jsonFiles = Directory.GetFiles(resultDir, "*.json")
                .OrderBy(f => File.GetCreationTime(f))
                .ToList();

            if (jsonFiles.Count == 0)
            {
                return Task.FromResult(DeviceResult<DataPayload>.Fail("U 盘中暂无试验数据文件"));
            }

            // 读取第一个结果文件（后续可扩展为批量）
            var firstFile = jsonFiles.First();
            var json = File.ReadAllText(firstFile, System.Text.Encoding.UTF8);
            var payload = DataPayload.FromJson(json);

            if (payload == null)
            {
                return Task.FromResult(DeviceResult<DataPayload>.Fail($"文件解析失败：{Path.GetFileName(firstFile)}"));
            }

            // 归档：移动到 archive/ 目录，保留原始文件名
            var archiveFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(firstFile)}";
            var archivePath = Path.Combine(archiveDir, archiveFileName);
            File.Move(firstFile, archivePath);

            return Task.FromResult(DeviceResult<DataPayload>.Success(payload));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult<DataPayload>.Fail($"读取试验数据失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 检查 U 盘状态
    /// </summary>
    public Task<DeviceStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        if (_drivePath == null)
        {
            return Task.FromResult(new DeviceStatus
            {
                IsOnline = false,
                Detail = "未连接"
            });
        }

        bool exists = Directory.Exists(_drivePath);
        DriveInfo? info = null;
        if (exists)
        {
            try { info = new DriveInfo(_drivePath.Substring(0, 1)); }
            catch (Exception ex) { Log.Debug(ex, "获取驱动器信息失败: {DrivePath}", _drivePath); }
        }

        return Task.FromResult(new DeviceStatus
        {
            IsOnline = exists,
            DeviceId = _drivePath,
            FirmwareVersion = string.Empty,
            LastHeartbeat = DateTime.Now,
            Detail = info != null
                ? $"{info.DriveFormat}，可用空间 {info.AvailableFreeSpace / 1024 / 1024} MB"
                : (exists ? "可访问" : "不可访问")
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (Status == ConnectionStatus.Online)
            {
                Status = ConnectionStatus.Offline;
                OnStateChanged(ConnectionStatus.Online, ConnectionStatus.Offline, "已断开 U 盘连接");
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
