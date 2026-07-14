using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 项目服务
/// </summary>
public sealed class ProjectService
{
    private readonly AppDbContext _context;

    public ProjectService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取所有项目
    /// </summary>
    public async Task<List<Project>> GetAllAsync()
    {
        return await _context.Projects
            .OrderBy(p => p.Code)
            .ToListAsync();
    }

    /// <summary>
    /// 获取启用的项目
    /// </summary>
    public async Task<List<Project>> GetEnabledAsync()
    {
        return await _context.Projects
            .Where(p => p.Status == EnabledStatus.Enabled)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    /// <summary>
    /// 根据编号获取项目
    /// </summary>
    public async Task<Project?> GetByCodeAsync(string code)
    {
        return await _context.Projects
            .Include(p => p.Units)
            .FirstOrDefaultAsync(p => p.Code == code);
    }

    /// <summary>
    /// 添加项目
    /// </summary>
    public async Task<Project> AddAsync(string code, string name, string? remark)
    {
        var trimmedCode = code.Trim();
        var trimmedName = name.Trim();

        // 用 Trim 后的值查重，避免带首尾空格的编号绕过检查、最终撞库主键抛出不友好异常
        if (await _context.Projects.AnyAsync(p => p.Code == trimmedCode || p.Name == trimmedName))
        {
            throw new InvalidOperationException("项目编号或名称已存在");
        }

        var project = new Project
        {
            Code = trimmedCode,
            Name = trimmedName,
            Status = EnabledStatus.Enabled,
            Remark = remark?.Trim()
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync();
        return project;
    }

    /// <summary>
    /// 更新项目
    /// </summary>
    public async Task UpdateAsync(Project project)
    {
        project.UpdatedAt = DateTime.Now;
        _context.Projects.Update(project);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 删除项目（级联删除机组、试验对象路径节点和试验记录）
    /// </summary>
    public async Task<bool> DeleteAsync(string code)
    {
        var project = await GetByCodeAsync(code);
        if (project == null) return false;

        // 获取项目下的所有机组
        var units = await _context.Units
            .Where(u => u.ProjectCode == code)
            .ToListAsync();

        var unitCodes = units.Select(u => u.Code).ToList();

        // 删除所有机组下的试验对象路径节点
        var pathNodes = await _context.TestObjectPathNodes
            .Where(n => unitCodes.Contains(n.UnitCode))
            .ToListAsync();
        _context.TestObjectPathNodes.RemoveRange(pathNodes);

        // 删除所有机组下的试验记录
        var testRecords = await _context.TestRecords
            .Where(r => unitCodes.Contains(r.UnitCode))
            .ToListAsync();
        _context.TestRecords.RemoveRange(testRecords);

        // 删除机组
        _context.Units.RemoveRange(units);

        // 删除项目
        _context.Projects.Remove(project);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 启用/停用项目
    /// </summary>
    public async Task SetStatusAsync(string code, EnabledStatus status)
    {
        var project = await GetByCodeAsync(code);
        if (project == null) return;

        project.Status = status;
        project.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
    }
}
