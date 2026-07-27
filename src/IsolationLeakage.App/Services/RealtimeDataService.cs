using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 实时监视曲线数据服务
/// 每个操作使用独立的 DbContext，支持并发访问
/// </summary>
public class RealtimeDataService
{
    // 不再持有单例 DbContext，每个操作创建独立上下文

    /// <summary>
    /// 创建监视会话
    /// </summary>
    public async Task<RealtimeCurveData> CreateSessionAsync(
        string? projectCode = null,
        string? unitCode = null,
        string? objectCode = null,
        int sampleIntervalMs = 500)
    {
        using var context = DbContextFactory.CreateDbContext();

        var sessionCode = $"RT-{DateTime.Now:yyyyMMdd-HHmmssfff}";

        var session = new RealtimeCurveData
        {
            SessionCode = sessionCode,
            ProjectCode = projectCode,
            UnitCode = unitCode,
            ObjectCode = objectCode,
            SampleIntervalMs = sampleIntervalMs,
            PointCount = 0,
            StartedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            Operator = Services.Security.UserSession.Current?.User.UserName ?? "system",
        };

        context.RealtimeCurveData.Add(session);
        await context.SaveChangesAsync();

        return session;
    }

    /// <summary>
    /// 保存曲线数据（upsert，按 SessionCode 更新）
    /// DB 写入失败时自动缓冲到 DataBufferService，恢复后用新的 DbContext 补写。
    /// </summary>
    public async Task SaveCurveAsync(
        string sessionCode,
        double[] pressurePoints,
        double[] flowPoints,
        double[] tempPoints,
        int pointCount,
        DateTime? endedAt = null)
    {
        // 清理 NaN / Infinity（JSON 不支持这些值），记录传感器故障警告
        var (pressureClean, pressureInvalidCount) = CleanValues(pressurePoints, "Pressure");
        var (flowClean, flowInvalidCount) = CleanValues(flowPoints, "Flow");
        var (tempClean, tempInvalidCount) = CleanValues(tempPoints, "Temp");

        // 如果有无效数据，记录警告（可能是传感器故障）
        if (pressureInvalidCount + flowInvalidCount + tempInvalidCount > 0)
        {
            Log.Warning(
                "[RealtimeDataService] 传感器数据异常: Pressure无效={PressureInvalid}, Flow无效={FlowInvalid}, Temp无效={TempInvalid} — 可能为传感器故障或通讯中断",
                pressureInvalidCount, flowInvalidCount, tempInvalidCount);
        }

        // 估算数据大小（用于缓冲区内存限制）
        var estimatedBytes = (pressureClean.Length + flowClean.Length + tempClean.Length) * sizeof(double) + 512;

        await DataBufferService.Instance.SaveOrBufferAsync(
            DataBufferService.BufferOperationType.SaveRealtimeData,
            $"实时曲线 session={sessionCode}, points={pointCount}",
            estimatedBytes,
            // 首次保存：创建新的 DbContext
            async () =>
            {
                await DoSaveCurveAsync(sessionCode, pressureClean, flowClean, tempClean, pointCount, endedAt);
            },
            // 重试工厂：切换 DB 后用新的 DbContext 重新查询实体再保存
            async (createDbContext) =>
            {
                using var newContext = createDbContext();
                var session = await newContext.RealtimeCurveData
                    .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);
                if (session == null)
                {
                    return false;
                }

                ApplyCurveData(session, pressureClean, flowClean, tempClean, pointCount, endedAt);
                await newContext.SaveChangesAsync();
                return true;
            });
    }

    /// <summary>
    /// 执行曲线数据保存（创建新的 DbContext）
    /// </summary>
    private async Task DoSaveCurveAsync(
        string sessionCode,
        double?[] pressureClean,
        double?[] flowClean,
        double?[] tempClean,
        int pointCount,
        DateTime? endedAt)
    {
        using var context = DbContextFactory.CreateDbContext();

        var session = await context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);
        if (session == null) return;

        ApplyCurveData(session, pressureClean, flowClean, tempClean, pointCount, endedAt);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// 将曲线数据应用到会话实体
    /// </summary>
    private static void ApplyCurveData(
        RealtimeCurveData session,
        double?[] pressureClean,
        double?[] flowClean,
        double?[] tempClean,
        int pointCount,
        DateTime? endedAt)
    {
        session.PressureCurveJson = JsonSerializer.Serialize(pressureClean);
        session.FlowCurveJson = JsonSerializer.Serialize(flowClean);
        session.TempCurveJson = JsonSerializer.Serialize(tempClean);
        session.PointCount = pointCount;

        // 计算极值时排除 null（传感器故障标记）
        var validPressure = pressureClean.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (validPressure.Length > 0)
        {
            session.PressureMin = (decimal)validPressure.Min();
            session.PressureMax = (decimal)validPressure.Max();
        }
        var validFlow = flowClean.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (validFlow.Length > 0)
        {
            session.FlowMin = (decimal)validFlow.Min();
            session.FlowMax = (decimal)validFlow.Max();
        }
        var validTemp = tempClean.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (validTemp.Length > 0)
        {
            session.TempMin = (decimal)validTemp.Min();
            session.TempMax = (decimal)validTemp.Max();
        }
        if (endedAt.HasValue)
        {
            session.EndedAt = endedAt.Value;
        }
    }

    /// <summary>
    /// 关闭监视会话
    /// </summary>
    public async Task CloseSessionAsync(string sessionCode)
    {
        using var context = DbContextFactory.CreateDbContext();

        var session = await context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);

        if (session != null)
        {
            session.EndedAt = DateTime.Now;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 获取会话信息
    /// </summary>
    public async Task<RealtimeCurveData?> GetSessionAsync(string sessionCode)
    {
        using var context = DbContextFactory.CreateDbContext();

        return await context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);
    }

    /// <summary>
    /// 清理数组中的 NaN / Infinity，替换为 null（JSON 序列化为 null）
    /// 返回 (清理后的数组, 无效值计数)，调用方应根据无效计数决定是否报警
    /// </summary>
    private static (double?[] cleaned, int invalidCount) CleanValues(double[] values, string channelName)
    {
        var cleaned = new double?[values.Length];
        int invalidCount = 0;
        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                cleaned[i] = null; // null 表示传感器故障/通讯中断，不掩盖为 0
                invalidCount++;
            }
            else
            {
                cleaned[i] = v;
            }
        }
        return (cleaned, invalidCount);
    }
}
