using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 实时监视曲线数据服务
/// </summary>
public class RealtimeDataService
{
    private readonly AppDbContext _context;

    public RealtimeDataService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 创建监视会话
    /// </summary>
    public async Task<RealtimeCurveData> CreateSessionAsync(
        string? projectCode = null,
        string? unitCode = null,
        string? objectCode = null,
        int sampleIntervalMs = 500)
    {
        var sessionCode = $"RT-{DateTime.Now:yyyyMMdd-HHmmss}";

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

        _context.RealtimeCurveData.Add(session);
        await _context.SaveChangesAsync();

        return session;
    }

    /// <summary>
    /// 保存曲线数据（upsert，按 SessionCode 更新）
    /// </summary>
    public async Task SaveCurveAsync(
        string sessionCode,
        double[] pressurePoints,
        double[] flowPoints,
        double[] tempPoints,
        int pointCount,
        DateTime? endedAt = null)
    {
        var session = await _context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);

        if (session == null) return;

        // 清理 NaN / Infinity（JSON 不支持这些值）
        var pressureClean = CleanValues(pressurePoints);
        var flowClean = CleanValues(flowPoints);
        var tempClean = CleanValues(tempPoints);

        session.PressureCurveJson = JsonSerializer.Serialize(pressureClean);
        session.FlowCurveJson = JsonSerializer.Serialize(flowClean);
        session.TempCurveJson = JsonSerializer.Serialize(tempClean);
        session.PointCount = pointCount;

        if (pressureClean.Length > 0)
        {
            session.PressureMin = (decimal)pressureClean.Min();
            session.PressureMax = (decimal)pressureClean.Max();
        }
        if (flowClean.Length > 0)
        {
            session.FlowMin = (decimal)flowClean.Min();
            session.FlowMax = (decimal)flowClean.Max();
        }
        if (tempClean.Length > 0)
        {
            session.TempMin = (decimal)tempClean.Min();
            session.TempMax = (decimal)tempClean.Max();
        }

        if (endedAt.HasValue)
        {
            session.EndedAt = endedAt.Value;
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 关闭监视会话
    /// </summary>
    public async Task CloseSessionAsync(string sessionCode)
    {
        var session = await _context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);

        if (session != null)
        {
            session.EndedAt = DateTime.Now;
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 获取会话信息
    /// </summary>
    public async Task<RealtimeCurveData?> GetSessionAsync(string sessionCode)
    {
        return await _context.RealtimeCurveData
            .FirstOrDefaultAsync(s => s.SessionCode == sessionCode);
    }

    /// <summary>
    /// 清理数组中的 NaN / Infinity，替换为 0（JSON 不支持这些值）
    /// </summary>
    private static double[] CleanValues(double[] values)
    {
        var cleaned = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            cleaned[i] = double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
        }
        return cleaned;
    }
}
