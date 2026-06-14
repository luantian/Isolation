using IsolationLeakage.App.Data;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Tests;

/// <summary>
/// 数据库连接测试工具
/// </summary>
public static class DatabaseTest
{
    /// <summary>
    /// 测试数据库连接并返回连接状态
    /// </summary>
    public static async Task<(bool Success, string Message, int ProjectCount, int DeviceCount, int RecordCount)> TestConnectionAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 测试连接
            var canConnect = await context.Database.CanConnectAsync();
            if (!canConnect)
            {
                return (false, "无法连接到数据库", 0, 0, 0);
            }

            // 检查数据
            var projectCount = await context.Projects.CountAsync();
            var deviceCount = await context.MeasurementDevices.CountAsync();
            var recordCount = await context.TestRecords.CountAsync();

            return (true, "数据库连接成功", projectCount, deviceCount, recordCount);
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}", 0, 0, 0);
        }
    }
}
