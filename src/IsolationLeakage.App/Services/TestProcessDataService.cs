using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验过程数据服务（按需加载大字段）
/// </summary>
public sealed class TestProcessDataService
{
    private readonly AppDbContext _context;

    public TestProcessDataService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取过程数据（包含曲线数据）
    /// </summary>
    public async Task<TestProcessData?> GetByRecordCodeAsync(string recordCode)
    {
        return await _context.TestProcessData.FindAsync(recordCode);
    }

    /// <summary>
    /// 获取压力曲线数据
    /// </summary>
    public async Task<double[]?> GetPressureCurveAsync(string recordCode)
    {
        var data = await GetByRecordCodeAsync(recordCode);
        if (string.IsNullOrEmpty(data?.PressureCurveJson))
        {
            return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<double[]>(data.PressureCurveJson);
    }

    /// <summary>
    /// 获取流量曲线数据
    /// </summary>
    public async Task<double[]?> GetFlowCurveAsync(string recordCode)
    {
        var data = await GetByRecordCodeAsync(recordCode);
        if (string.IsNullOrEmpty(data?.FlowCurveJson))
        {
            return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<double[]>(data.FlowCurveJson);
    }

    /// <summary>
    /// 获取温度曲线数据
    /// </summary>
    public async Task<double[]?> GetTempCurveAsync(string recordCode)
    {
        var data = await GetByRecordCodeAsync(recordCode);
        if (string.IsNullOrEmpty(data?.TempCurveJson))
        {
            return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<double[]>(data.TempCurveJson);
    }

    /// <summary>
    /// 保存过程数据
    /// </summary>
    public async Task SaveAsync(TestProcessData processData)
    {
        var existing = await GetByRecordCodeAsync(processData.RecordCode);
        if (existing == null)
        {
            _context.TestProcessData.Add(processData);
        }
        else
        {
            existing.PressureCurveJson = processData.PressureCurveJson;
            existing.FlowCurveJson = processData.FlowCurveJson;
            existing.Flow2CurveJson = processData.Flow2CurveJson;
            existing.TempCurveJson = processData.TempCurveJson;
            existing.Pressure2CurveJson = processData.Pressure2CurveJson;
            existing.TimeAxisJson = processData.TimeAxisJson;
            existing.PressureMin = processData.PressureMin;
            existing.PressureMax = processData.PressureMax;
            existing.FlowMin = processData.FlowMin;
            existing.FlowMax = processData.FlowMax;
            existing.Flow2Min = processData.Flow2Min;
            existing.Flow2Max = processData.Flow2Max;
            existing.TempMin = processData.TempMin;
            existing.TempMax = processData.TempMax;
            existing.Pressure2Min = processData.Pressure2Min;
            existing.Pressure2Max = processData.Pressure2Max;
            existing.UpdatedAt = DateTime.Now;
        }
        await _context.SaveChangesAsync();
    }
}
