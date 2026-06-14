using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 操作日志服务
/// </summary>
public sealed class OperationLogService
{
    private readonly AppDbContext _context;

    public OperationLogService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 记录操作日志
    /// </summary>
    /// <param name="operationType">操作类型</param>
    /// <param name="userName">操作用户名</param>
    /// <param name="details">操作详情</param>
    /// <param name="result">操作结果</param>
    /// <param name="ipAddress">IP地址（可选）</param>
    public async Task LogAsync(
        string operationType,
        string userName,
        string details,
        string result,
        string? ipAddress = null)
    {
        var log = new LoginLog
        {
            UserName = userName,
            IsSuccess = result.Equals("Success", StringComparison.OrdinalIgnoreCase),
            FailReason = result.Equals("Success", StringComparison.OrdinalIgnoreCase) ? null : result,
            ClientIp = ipAddress,
            LoginTime = DateTime.Now,
            UserAgent = $"Operation: {operationType}, Details: {details}"
        };

        _context.LoginLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 分页获取操作日志
    /// </summary>
    /// <param name="operationType">操作类型筛选</param>
    /// <param name="userName">用户名筛选</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <param name="pageIndex">页码（从0开始）</param>
    /// <param name="pageSize">每页数量</param>
    /// <returns>操作日志列表</returns>
    public async Task<List<LoginLog>> GetPagedAsync(
        string? operationType = null,
        string? userName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int pageIndex = 0,
        int pageSize = 50)
    {
        var query = BuildQuery(operationType, userName, startTime, endTime);

        return await query
            .OrderByDescending(l => l.LoginTime)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// 获取符合条件的操作日志总数
    /// </summary>
    /// <param name="operationType">操作类型筛选</param>
    /// <param name="userName">用户名筛选</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>记录总数</returns>
    public async Task<int> CountAsync(
        string? operationType = null,
        string? userName = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var query = BuildQuery(operationType, userName, startTime, endTime);
        return await query.CountAsync();
    }

    /// <summary>
    /// 构建查询条件
    /// </summary>
    private IQueryable<LoginLog> BuildQuery(
        string? operationType,
        string? userName,
        DateTime? startTime,
        DateTime? endTime)
    {
        var query = _context.LoginLogs.AsQueryable();

        if (!string.IsNullOrEmpty(userName))
        {
            query = query.Where(l => l.UserName == userName);
        }

        if (startTime.HasValue)
        {
            query = query.Where(l => l.LoginTime >= startTime.Value);
        }

        if (endTime.HasValue)
        {
            query = query.Where(l => l.LoginTime <= endTime.Value);
        }

        // 操作类型通过 UserAgent 字段筛选（格式为 "Operation: {operationType}, ..."）
        if (!string.IsNullOrEmpty(operationType))
        {
            query = query.Where(l => l.UserAgent != null && l.UserAgent.Contains($"Operation: {operationType}"));
        }

        return query;
    }
}
