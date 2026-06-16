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

        session.PressureCurveJson = JsonSerializer.Serialize(pressurePoints);
        session.FlowCurveJson = JsonSerializer.Serialize(flowPoints);
        session.TempCurveJson = JsonSerializer.Serialize(tempPoints);
        session.PointCount = pointCount;

        if (pressurePoints.Length > 0)
        {
            session.PressureMin = (decimal)pressurePoints.Min();
            session.PressureMax = (decimal)pressurePoints.Max();
        }
        if (flowPoints.Length > 0)
        {
            session.FlowMin = (decimal)flowPoints.Min();
            session.FlowMax = (decimal)flowPoints.Max();
        }
        if (tempPoints.Length > 0)
        {
            session.TempMin = (decimal)tempPoints.Min();
            session.TempMax = (decimal)tempPoints.Max();
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
}
