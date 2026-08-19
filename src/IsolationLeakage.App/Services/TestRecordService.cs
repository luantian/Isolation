using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验记录服务
/// 每次操作独立创建短生命周期 DbContext（照 RecipeService 模式）：
/// 不再挂在共享单例上下文上——批量导入与单条上传并发操作同一单例会抛 EF
/// "second operation" 异常，故障切换后旧上下文也已释放。
/// </summary>
public sealed class TestRecordService
{
    public TestRecordService(AppDbContext? context = null)
    {
        // 不保存 context，每次操作独立创建
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
        using var context = DbContextFactory.CreateDbContext();
        var query = BuildQuery(context, projectCode, unitCode, objectCode, deviceCode, result, startTime, endTime);

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
        using var context = DbContextFactory.CreateDbContext();
        var query = BuildQuery(context, projectCode, unitCode, objectCode, deviceCode, result, startTime, endTime);
        return await query.CountAsync();
    }

    /// <summary>
    /// 根据编号获取试验记录
    /// </summary>
    public async Task<TestRecord?> GetByCodeAsync(string recordCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.TestObject)
            .Include(r => r.Device)
            .FirstOrDefaultAsync(r => r.RecordCode == recordCode);
    }

    /// <summary>
    /// 获取试验对象的历史试验记录（查重场景：AsNoTracking 防止批量导入时
    /// 数百条实体挂入上下文导致 ChangeTracker 无限膨胀、SaveChanges 越导越慢）
    /// </summary>
    public async Task<List<TestRecord>> GetByObjectAsync(string objectCode, int take = 100)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecords
            .AsNoTracking()
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
        using var context = DbContextFactory.CreateDbContext();

        if (await context.TestRecords.AnyAsync(r => r.RecordCode == record.RecordCode))
        {
            throw new InvalidOperationException("试验记录编号已存在");
        }

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            context.TestRecords.Add(record);

            if (processData != null)
            {
                processData.RecordCode = record.RecordCode;
                context.TestProcessData.Add(processData);
            }

            // 更新装置上传统计
            var device = await context.MeasurementDevices.FindAsync(record.DeviceCode);
            if (device != null)
            {
                device.UploadCount++;
                device.LastUploadTime = record.ImportTime;
                device.LastUploadResult = record.Result;
                device.UpdatedAt = DateTime.Now;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return record;
        }
        catch
        {
            await transaction.RollbackAsync();
            // 本方法使用短生命周期上下文，失败即随 using 销毁、变更跟踪器整个丢弃，
            // 不会再污染其它 SaveChanges（原单例上下文时代的 Detach 清理已无必要）
            throw;
        }
    }

    /// <summary>
    /// 删除试验记录
    /// </summary>
    public async Task<bool> DeleteAsync(string recordCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var record = await context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.TestObject)
            .Include(r => r.Device)
            .FirstOrDefaultAsync(r => r.RecordCode == recordCode);
        if (record == null) return false;

        context.TestRecords.Remove(record);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 获取试验对象的统计信息
    /// </summary>
    public async Task<(int TotalTests, int PassedTests, int FailedTests, decimal PassRate)> GetObjectStatisticsAsync(string objectCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        var records = await context.TestRecords
            .AsNoTracking()
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
    private static IQueryable<TestRecord> BuildQuery(
        AppDbContext context,
        string? projectCode,
        string? unitCode,
        string? objectCode,
        string? deviceCode,
        TestResult? result,
        DateTime? startTime,
        DateTime? endTime)
    {
        var query = context.TestRecords.AsQueryable();

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
