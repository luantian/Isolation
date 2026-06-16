using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 操作日志服务（使用独立的 OperationLogs 表）
/// </summary>
public sealed class OperationLogService
{
    /// <summary>默认保留天数</summary>
    public const int DefaultRetentionDays = 90;

    private readonly AppDbContext _context;

    public OperationLogService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 记录操作日志
    /// </summary>
    public async Task LogAsync(
        string operationType,
        string userName,
        string details,
        string result,
        string? ipAddress = null)
    {
        var log = new OperationLog
        {
            OperationType = operationType,
            UserName = userName,
            Details = details,
            Result = result,
            IpAddress = ipAddress,
            OperationTime = DateTime.Now
        };

        _context.OperationLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 分页获取操作日志
    /// </summary>
    public async Task<List<OperationLog>> GetPagedAsync(
        string? operationType = null,
        string? userName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int pageIndex = 0,
        int pageSize = 50)
    {
        var query = BuildQuery(operationType, userName, startTime, endTime);

        return await query
            .OrderByDescending(l => l.OperationTime)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// 获取符合条件的操作日志总数
    /// </summary>
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
    /// 获取指定日期之前的日志总数（清理前预览）
    /// </summary>
    public async Task<int> GetCountBeforeAsync(DateTime cutoffDate)
    {
        return await _context.OperationLogs
            .CountAsync(l => l.OperationTime < cutoffDate);
    }

    /// <summary>
    /// 清理指定日期之前的日志
    /// </summary>
    /// <param name="cutoffDate">清理此日期之前的所有日志</param>
    /// <returns>删除的记录数</returns>
    public async Task<int> CleanupOldLogsAsync(DateTime cutoffDate)
    {
        var oldLogs = _context.OperationLogs
            .Where(l => l.OperationTime < cutoffDate);

        int count = await oldLogs.CountAsync();
        if (count == 0) return 0;

        _context.OperationLogs.RemoveRange(oldLogs);
        await _context.SaveChangesAsync();
        return count;
    }

    /// <summary>
    /// 导出指定日期范围内的日志到 CSV 文件
    /// </summary>
    public async Task<string> ExportToCsvAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? exportPath = null)
    {
        var query = _context.OperationLogs.AsQueryable();
        if (startTime.HasValue)
            query = query.Where(l => l.OperationTime >= startTime.Value);
        if (endTime.HasValue)
            query = query.Where(l => l.OperationTime <= endTime.Value);

        var logs = await query.OrderByDescending(l => l.OperationTime).ToListAsync();

        exportPath ??= Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "exports",
            $"OperationLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var dir = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("LogId,OperationType,UserName,Details,Result,IpAddress,OperationTime");

        foreach (var log in logs)
        {
            sb.AppendLine($"{log.LogId}," +
                $"\"{EscapeCsv(log.OperationType)}\"," +
                $"\"{EscapeCsv(log.UserName)}\"," +
                $"\"{EscapeCsv(log.Details ?? "")}\"," +
                $"\"{EscapeCsv(log.Result)}\"," +
                $"\"{EscapeCsv(log.IpAddress ?? "")}\"," +
                $"\"{log.OperationTime:yyyy-MM-dd HH:mm:ss}\"");
        }

        await File.WriteAllTextAsync(exportPath, sb.ToString(), Encoding.UTF8);
        return exportPath;
    }

    /// <summary>
    /// 构建查询条件
    /// </summary>
    private IQueryable<OperationLog> BuildQuery(
        string? operationType,
        string? userName,
        DateTime? startTime,
        DateTime? endTime)
    {
        var query = _context.OperationLogs.AsQueryable();

        if (!string.IsNullOrEmpty(operationType))
        {
            query = query.Where(l => l.OperationType == operationType);
        }

        if (!string.IsNullOrEmpty(userName))
        {
            query = query.Where(l => l.UserName == userName);
        }

        if (startTime.HasValue)
        {
            query = query.Where(l => l.OperationTime >= startTime.Value);
        }

        if (endTime.HasValue)
        {
            query = query.Where(l => l.OperationTime <= endTime.Value);
        }

        return query;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\"", "\"\"");
    }
}
