using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 测量装置服务
/// </summary>
public sealed class MeasurementDeviceService
{
    private readonly AppDbContext _context;

    public MeasurementDeviceService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取所有装置
    /// </summary>
    public async Task<List<MeasurementDevice>> GetAllAsync()
    {
        return await _context.MeasurementDevices
            .OrderBy(d => d.DeviceCode)
            .ToListAsync();
    }

    /// <summary>
    /// 获取启用的装置
    /// </summary>
    public async Task<List<MeasurementDevice>> GetEnabledAsync()
    {
        return await _context.MeasurementDevices
            .Where(d => d.EnabledStatus == EnabledStatus.Enabled)
            .OrderBy(d => d.DeviceName)
            .ToListAsync();
    }

    /// <summary>
    /// 根据条件搜索装置
    /// </summary>
    public async Task<List<MeasurementDevice>> SearchAsync(
        string? keyword = null,
        CommunicationType? communicationType = null,
        EnabledStatus? status = null)
    {
        var query = _context.MeasurementDevices.AsQueryable();

        if (!string.IsNullOrEmpty(keyword))
        {
            keyword = keyword.Trim().ToLower();
            query = query.Where(d =>
                d.DeviceCode.ToLower().Contains(keyword) ||
                d.DeviceName.ToLower().Contains(keyword) ||
                (d.Ip != null && d.Ip.ToLower().Contains(keyword)) ||
                (d.SerialNumber != null && d.SerialNumber.ToLower().Contains(keyword)));
        }

        if (communicationType.HasValue)
        {
            query = query.Where(d => d.PrimaryCommunication == communicationType.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(d => d.EnabledStatus == status.Value);
        }

        return await query.OrderBy(d => d.DeviceCode).ToListAsync();
    }

    /// <summary>
    /// 根据编号获取装置
    /// </summary>
    public async Task<MeasurementDevice?> GetByCodeAsync(string deviceCode)
    {
        return await _context.MeasurementDevices
            .Include(d => d.TestRecords)
            .FirstOrDefaultAsync(d => d.DeviceCode == deviceCode);
    }

    /// <summary>
    /// 添加装置
    /// </summary>
    public async Task<MeasurementDevice> AddAsync(MeasurementDevice device)
    {
        if (await _context.MeasurementDevices.AnyAsync(d => d.DeviceCode == device.DeviceCode))
        {
            throw new InvalidOperationException("装置编号已存在");
        }

        _context.MeasurementDevices.Add(device);
        await _context.SaveChangesAsync();
        return device;
    }

    /// <summary>
    /// 更新装置
    /// </summary>
    public async Task UpdateAsync(MeasurementDevice device)
    {
        device.UpdatedAt = DateTime.Now;
        _context.MeasurementDevices.Update(device);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 设置装置启用/停用状态
    /// </summary>
    public async Task SetEnabledStatusAsync(string deviceCode, EnabledStatus status)
    {
        var device = await GetByCodeAsync(deviceCode);
        if (device == null) return;

        device.EnabledStatus = status;
        device.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 更新装置连接状态
    /// </summary>
    public async Task UpdateConnectionStatusAsync(string deviceCode, ConnectionStatus status, DateTime? syncTime = null)
    {
        var device = await GetByCodeAsync(deviceCode);
        if (device == null) return;

        device.ConnectionStatus = status;
        if (syncTime.HasValue)
        {
            device.LastSyncTime = syncTime.Value;
        }
        device.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 记录上传
    /// </summary>
    public async Task RecordUploadAsync(string deviceCode, TestResult uploadResult, DateTime uploadTime)
    {
        var device = await GetByCodeAsync(deviceCode);
        if (device == null) return;

        device.UploadCount++;
        device.LastUploadTime = uploadTime;
        device.LastUploadResult = uploadResult;
        device.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
    }
}
