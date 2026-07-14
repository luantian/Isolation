using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 机组服务
/// </summary>
public sealed class UnitService
{
    private readonly AppDbContext _context;

    public UnitService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取项目下的所有机组
    /// </summary>
    public async Task<List<Unit>> GetByProjectAsync(string projectCode)
    {
        return await _context.Units
            .Where(u => u.ProjectCode == projectCode)
            .OrderBy(u => u.Code)
            .ToListAsync();
    }

    /// <summary>
    /// 获取启用的机组
    /// </summary>
    public async Task<List<Unit>> GetEnabledByProjectAsync(string projectCode)
    {
        return await _context.Units
            .Where(u => u.ProjectCode == projectCode && u.Status == EnabledStatus.Enabled)
            .OrderBy(u => u.Name)
            .ToListAsync();
    }

    /// <summary>
    /// 根据编号获取机组
    /// </summary>
    public async Task<Unit?> GetByCodeAsync(string code)
    {
        return await _context.Units
            .Include(u => u.Project)
            .Include(u => u.PathNodes)
            .FirstOrDefaultAsync(u => u.Code == code);
    }

    /// <summary>
    /// 添加机组
    /// </summary>
    public async Task<Unit> AddAsync(string projectCode, string code, string name, string? remark)
    {
        var trimmedCode = code.Trim();
        var trimmedName = name.Trim();

        // 编号是全局主键，必须全局唯一（不同项目也不能重复），否则会在保存时抛出
        // 难以理解的数据库主键异常。先在应用层拦下，给出友好提示。
        if (await _context.Units.AnyAsync(u => u.Code == trimmedCode))
        {
            throw new InvalidOperationException($"机组编号 {trimmedCode} 已存在（编号全局唯一，不同项目也不能重复）");
        }

        // 名称在同一项目内唯一即可
        if (await _context.Units.AnyAsync(u => u.ProjectCode == projectCode && u.Name == trimmedName))
        {
            throw new InvalidOperationException("当前项目下机组名称已存在");
        }

        var unit = new Unit
        {
            ProjectCode = projectCode,
            Code = trimmedCode,
            Name = trimmedName,
            Status = EnabledStatus.Enabled,
            Remark = remark?.Trim()
        };

        _context.Units.Add(unit);
        await _context.SaveChangesAsync();
        return unit;
    }

    /// <summary>
    /// 更新机组
    /// </summary>
    public async Task UpdateAsync(Unit unit)
    {
        unit.UpdatedAt = DateTime.Now;
        _context.Units.Update(unit);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 删除机组（级联删除试验对象路径节点和试验记录）
    /// </summary>
    public async Task<bool> DeleteAsync(string code)
    {
        var unit = await GetByCodeAsync(code);
        if (unit == null) return false;

        // 删除该机组下的所有试验对象路径节点
        var pathNodes = await _context.TestObjectPathNodes
            .Where(n => n.UnitCode == code)
            .ToListAsync();
        _context.TestObjectPathNodes.RemoveRange(pathNodes);

        // 删除该机组下的所有试验记录
        var testRecords = await _context.TestRecords
            .Where(r => r.UnitCode == code)
            .ToListAsync();
        _context.TestRecords.RemoveRange(testRecords);

        // 删除机组
        _context.Units.Remove(unit);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 启用/停用机组
    /// </summary>
    public async Task SetStatusAsync(string code, EnabledStatus status)
    {
        var unit = await GetByCodeAsync(code);
        if (unit == null) return;

        unit.Status = status;
        unit.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
    }
}
