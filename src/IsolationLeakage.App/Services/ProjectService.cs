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
        if (await _context.Projects.AnyAsync(p => p.Code == code || p.Name == name))
        {
            throw new InvalidOperationException("项目编号或名称已存在");
        }

        var project = new Project
        {
            Code = code.Trim(),
            Name = name.Trim(),
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
    /// 删除项目（删除保护：有机组关联时不允许删除）
    /// </summary>
    public async Task<bool> DeleteAsync(string code)
    {
        var project = await GetByCodeAsync(code);
        if (project == null) return false;

        // 删除保护：检查是否有关联机组成
        if (await _context.Units.AnyAsync(u => u.ProjectCode == code))
        {
            throw new InvalidOperationException("该项目下有机组，不允许删除");
        }

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
