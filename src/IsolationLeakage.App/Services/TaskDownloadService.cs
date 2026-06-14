using IsolationLeakage.App.Communication;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 任务下载服务：创建试验对象下载任务、下发至测量装置、记录历史
/// </summary>
public sealed class TaskDownloadService
{
    private readonly AppDbContext _context;
    private readonly ConnectionManager _connectionManager;

    public TaskDownloadService(AppDbContext context, ConnectionManager connectionManager)
    {
        _context = context;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// 为选中的试验对象创建下载任务
    /// </summary>
    /// <param name="objectCodes">试验对象编码列表</param>
    /// <param name="deviceCode">测量装置编码</param>
    /// <returns>创建的任务载荷</returns>
    public async Task<TaskPayload> CreateTaskAsync(List<string> objectCodes, string deviceCode)
    {
        if (objectCodes == null || !objectCodes.Any())
        {
            throw new ArgumentException("至少选择一个试验对象", nameof(objectCodes));
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            throw new ArgumentException("测量装置编码不能为空", nameof(deviceCode));
        }

        // 验证测量装置是否存在
        var device = await _context.MeasurementDevices.FindAsync(deviceCode);
        if (device == null)
        {
            throw new InvalidOperationException($"测量装置 {deviceCode} 不存在");
        }

        // 获取试验对象节点信息
        var pathNodes = await _context.TestObjectPathNodes
            .Where(n => objectCodes.Contains(n.Code))
            .ToListAsync();

        if (pathNodes.Count != objectCodes.Count)
        {
            var foundCodes = pathNodes.Select(n => n.Code).ToHashSet();
            var missingCodes = objectCodes.Where(c => !foundCodes.Contains(c)).ToList();
            throw new InvalidOperationException($"以下试验对象不存在: {string.Join(", ", missingCodes)}");
        }

        // 生成任务 ID
        var taskId = $"TASK-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N[..8]}";

        // 构建任务载荷条目
        var objects = pathNodes.Select(node => new TestObjectEntry
        {
            Code = node.Code,
            Name = node.Name,
            ObjectType = node.NodeType,
            LeakageLimit = node.LeakageLimit,
            TestPressure = node.TestPressure,
            ValveType = node.ValveType,
            ComponentType = node.ComponentType,
        }).ToList();

        // 创建任务载荷
        var payload = new TaskPayload
        {
            TaskId = taskId,
            Objects = objects,
            GeneratedAt = DateTime.Now,
            Operator = "当前用户", // 可从认证服务获取
            Remark = null,
        };

        // 保存任务到数据库
        var downloadTask = new TaskDownloadRecord
        {
            TaskId = taskId,
            DeviceCode = deviceCode,
            ObjectCodes = string.Join(",", objectCodes),
            ObjectCount = objects.Count,
            PayloadJson = payload.ToJson(),
            Status = TaskDownloadStatus.Created,
            CreatedAt = DateTime.Now,
        };

        _context.TaskDownloadRecords.Add(downloadTask);
        await _context.SaveChangesAsync();

        return payload;
    }

    /// <summary>
    /// 将任务数据下发至测量装置
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <param name="deviceCode">测量装置编码</param>
    /// <returns>下发结果</returns>
    public async Task<TaskDownloadResult> DownloadTaskAsync(string taskId, string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("任务 ID 不能为空", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            throw new ArgumentException("测量装置编码不能为空", nameof(deviceCode));
        }

        // 获取任务记录
        var taskRecord = await _context.TaskDownloadRecords
            .FirstOrDefaultAsync(r => r.TaskId == taskId);

        if (taskRecord == null)
        {
            throw new InvalidOperationException($"任务 {taskId} 不存在");
        }

        // 反序列化任务载荷
        var payload = TaskPayload.FromJson(taskRecord.PayloadJson);
        if (payload == null)
        {
            throw new InvalidOperationException("任务数据解析失败");
        }

        // 获取设备连接
        var connection = _connectionManager.GetConnection(deviceCode);
        if (connection == null)
        {
            throw new InvalidOperationException($"设备 {deviceCode} 未连接，请先建立连接");
        }

        // 更新任务状态为发送中
        taskRecord.Status = TaskDownloadStatus.Sending;
        taskRecord.SentAt = DateTime.Now;
        await _context.SaveChangesAsync();

        try
        {
            // 发送任务至设备
            var result = await connection.SendTaskAsync(payload);

            if (result.IsSuccess && result.Data != null)
            {
                // 下发成功
                taskRecord.Status = TaskDownloadStatus.Success;
                taskRecord.CompletedAt = DateTime.Now;
                taskRecord.TotalObjects = result.Data.TotalObjects;
                taskRecord.SentCount = result.Data.SentCount;
                taskRecord.FailedCount = result.Data.FailedCount;
                taskRecord.FailedObjects = result.Data.FailedObjects.Any()
                    ? string.Join(",", result.Data.FailedObjects)
                    : null;
                taskRecord.Detail = result.Data.Detail;
            }
            else
            {
                // 下发失败
                taskRecord.Status = TaskDownloadStatus.Failed;
                taskRecord.Detail = result.Error;
            }

            await _context.SaveChangesAsync();

            return new TaskDownloadResult
            {
                TaskId = taskId,
                Success = result.IsSuccess,
                TotalObjects = result.Data?.TotalObjects ?? 0,
                SentCount = result.Data?.SentCount ?? 0,
                FailedCount = result.Data?.FailedCount ?? 0,
                FailedObjects = result.Data?.FailedObjects ?? [],
                Message = result.Error,
            };
        }
        catch (Exception ex)
        {
            // 异常处理
            taskRecord.Status = TaskDownloadStatus.Failed;
            taskRecord.Detail = ex.Message;
            await _context.SaveChangesAsync();

            return new TaskDownloadResult
            {
                TaskId = taskId,
                Success = false,
                TotalObjects = payload.Objects.Count,
                SentCount = 0,
                FailedCount = payload.Objects.Count,
                FailedObjects = [],
                Message = $"下发失败: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// 获取任务历史记录
    /// </summary>
    /// <param name="deviceCode">可选的装置编码过滤</param>
    /// <param name="status">可选的状态过滤</param>
    /// <param name="pageIndex">页码（从 0 开始）</param>
    /// <param name="pageSize">每页大小</param>
    /// <returns>任务历史记录列表</returns>
    public async Task<List<TaskDownloadRecord>> GetTaskHistoryAsync(
        string? deviceCode = null,
        TaskDownloadStatus? status = null,
        int pageIndex = 0,
        int pageSize = 50)
    {
        var query = _context.TaskDownloadRecords.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceCode))
        {
            query = query.Where(r => r.DeviceCode == deviceCode);
        }

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// 获取任务总数
    /// </summary>
    /// <param name="deviceCode">可选的装置编码过滤</param>
    /// <param name="status">可选的状态过滤</param>
    /// <returns>任务总数</returns>
    public async Task<int> GetTaskCountAsync(
        string? deviceCode = null,
        TaskDownloadStatus? status = null)
    {
        var query = _context.TaskDownloadRecords.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceCode))
        {
            query = query.Where(r => r.DeviceCode == deviceCode);
        }

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        return await query.CountAsync();
    }

    /// <summary>
    /// 根据任务 ID 获取任务详情
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>任务详情</returns>
    public async Task<TaskDownloadRecord?> GetTaskByIdAsync(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        return await _context.TaskDownloadRecords
            .FirstOrDefaultAsync(r => r.TaskId == taskId);
    }
}

/// <summary>
/// 任务下发结果
/// </summary>
public sealed class TaskDownloadResult
{
    /// <summary>任务 ID</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>总对象数</summary>
    public int TotalObjects { get; init; }

    /// <summary>成功发送数</summary>
    public int SentCount { get; init; }

    /// <summary>失败数</summary>
    public int FailedCount { get; init; }

    /// <summary>失败对象列表</summary>
    public List<string> FailedObjects { get; init; } = [];

    /// <summary>结果消息</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 任务下载状态枚举
/// </summary>
public enum TaskDownloadStatus
{
    /// <summary>已创建</summary>
    Created = 0,

    /// <summary>发送中</summary>
    Sending = 1,

    /// <summary>发送成功</summary>
    Success = 2,

    /// <summary>发送失败</summary>
    Failed = 3,
}

/// <summary>
/// 任务下载记录表
/// </summary>
public sealed class TaskDownloadRecord
{
    /// <summary>主键 ID</summary>
    [System.ComponentModel.DataAnnotations.Key]
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>测量装置编码</summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(50)]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>试验对象编码列表（逗号分隔）</summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string ObjectCodes { get; set; } = string.Empty;

    /// <summary>对象数量</summary>
    public int ObjectCount { get; set; }

    /// <summary>任务载荷 JSON</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>任务状态</summary>
    public TaskDownloadStatus Status { get; set; } = TaskDownloadStatus.Created;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>发送时间</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>完成时间</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>总对象数（来自设备返回）</summary>
    public int? TotalObjects { get; set; }

    /// <summary>成功发送数（来自设备返回）</summary>
    public int? SentCount { get; set; }

    /// <summary>失败数（来自设备返回）</summary>
    public int? FailedCount { get; set; }

    /// <summary>失败对象列表（逗号分隔）</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? FailedObjects { get; set; }

    /// <summary>详细信息</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? Detail { get; set; }

    // 导航属性：测量装置
    public MeasurementDevice? Device { get; set; }

    /// <summary>状态显示文字</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string StatusText => Status switch
    {
        TaskDownloadStatus.Created => "已创建",
        TaskDownloadStatus.Sending => "发送中",
        TaskDownloadStatus.Success => "成功",
        TaskDownloadStatus.Failed => "失败",
        _ => "未知"
    };
}
