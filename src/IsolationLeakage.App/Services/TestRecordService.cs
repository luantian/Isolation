using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验记录服务
/// </summary>
public sealed class TestRecordService
{
    private readonly AppDbContext _context;

    public TestRecordService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 分页获取试验记录
    /// </summary>
    public async Task<List<TestRecord>> GetPagedAsync(
        string? projectCode = null,
        string? unitCode = null,
        string? objectCode = null,
        string? deviceCode = null,
        TestResult? result = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int pageIndex = 0,
        int pageSize = 50)
    {
        var query = BuildQuery(projectCode, unitCode, objectCode, deviceCode, result, startTime, endTime);

        return await query
            .OrderByDescending(r => r.TestTime)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// 获取记录总数
    /// </summary>
    public async Task<int> CountAsync(
        string? projectCode = null,
        string? unitCode = null,
        string? objectCode = null,
        string? deviceCode = null,
        TestResult? result = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var query = BuildQuery(projectCode, unitCode, objectCode, deviceCode, result, startTime, endTime);
        return await query.CountAsync();
    }

    /// <summary>
    /// 根据编号获取试验记录
    /// </summary>
    public async Task<TestRecord?> GetByCodeAsync(string recordCode)
    {
        return await _context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.TestObject)
            .Include(r => r.Device)
            .FirstOrDefaultAsync(r => r.RecordCode == recordCode);
    }

    /// <summary>
    /// 获取试验对象的历史试验记录
    /// </summary>
    public async Task<List<TestRecord>> GetByObjectAsync(string objectCode, int take = 100)
    {
        return await _context.TestRecords
            .Where(r => r.ObjectCode == objectCode)
            .OrderByDescending(r => r.TestTime)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>
    /// 添加试验记录（同时更新装置上传统计）
    /// </summary>
    public async Task<TestRecord> AddAsync(TestRecord record, TestProcessData? processData = null)
    {
        if (await _context.TestRecords.AnyAsync(r => r.RecordCode == record.RecordCode))
        {
            throw new InvalidOperationException("试验记录编号已存在");
        }

        // 装置引用提到 try 外，便于失败时从变更跟踪器摘除，避免污染后续 SaveChanges
        MeasurementDevice? device = null;
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            _context.TestRecords.Add(record);

            if (processData != null)
            {
                processData.RecordCode = record.RecordCode;
                _context.TestProcessData.Add(processData);
            }

            // 更新装置上传统计
            device = await _context.MeasurementDevices.FindAsync(record.DeviceCode);
            if (device != null)
            {
                device.UploadCount++;
                device.LastUploadTime = record.ImportTime;
                device.LastUploadResult = record.Result;
                device.UpdatedAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return record;
        }
        catch
        {
            await transaction.RollbackAsync();

            // EF Core 回滚数据库事务不会清理变更跟踪器：失败的实体仍以 Added/Modified 状态
            // 挂在这个长生命周期单例上下文里，下一次任何 SaveChanges 都会尝试重新写入它们，
            // 造成本条已回滚的记录连累后续记录（表现为原始 FK 外键错误）。这里手动摘除。
            _context.Entry(record).State = EntityState.Detached;
            if (processData != null)
                _context.Entry(processData).State = EntityState.Detached;
            if (device != null)
                _context.Entry(device).State = EntityState.Detached;

            throw;
        }
    }

    /// <summary>
    /// 删除试验记录
    /// </summary>
    public async Task<bool> DeleteAsync(string recordCode)
    {
        var record = await GetByCodeAsync(recordCode);
        if (record == null) return false;

        _context.TestRecords.Remove(record);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 获取试验对象的统计信息
    /// </summary>
    public async Task<(int TotalTests, int PassedTests, int FailedTests, decimal PassRate)> GetObjectStatisticsAsync(string objectCode)
    {
        var records = await _context.TestRecords
            .Where(r => r.ObjectCode == objectCode)
            .Select(r => r.Result)
            .ToListAsync();

        if (!records.Any())
        {
            return (0, 0, 0, 0);
        }

        int passed = records.Count(r => r == TestResult.Pass);
        int failed = records.Count(r => r == TestResult.Fail);
        decimal passRate = (decimal)passed / records.Count * 100;

        return (records.Count, passed, failed, Math.Round(passRate, 2));
    }

    /// <summary>
    /// 构建查询
    /// </summary>
    private IQueryable<TestRecord> BuildQuery(
        string? projectCode,
        string? unitCode,
        string? objectCode,
        string? deviceCode,
        TestResult? result,
        DateTime? startTime,
        DateTime? endTime)
    {
        var query = _context.TestRecords.AsQueryable();

        if (!string.IsNullOrEmpty(projectCode))
        {
            query = query.Where(r => r.ProjectCode == projectCode);
        }

        if (!string.IsNullOrEmpty(unitCode))
        {
            query = query.Where(r => r.UnitCode == unitCode);
        }

        if (!string.IsNullOrEmpty(objectCode))
        {
            query = query.Where(r => r.ObjectCode == objectCode);
        }

        if (!string.IsNullOrEmpty(deviceCode))
        {
            query = query.Where(r => r.DeviceCode == deviceCode);
        }

        if (result.HasValue)
        {
            query = query.Where(r => r.Result == result.Value);
        }

        if (startTime.HasValue)
        {
            query = query.Where(r => r.TestTime >= startTime.Value);
        }

        if (endTime.HasValue)
        {
            query = query.Where(r => r.TestTime <= endTime.Value);
        }

        return query;
    }
}
