using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 角色服务（仿若依角色管理）
/// </summary>
public sealed class RoleService
{
    private readonly AppDbContext _context;

    public RoleService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<Role>> GetAllAsync()
    {
        return await _context.Roles
            .OrderBy(r => r.Sort)
            .ToListAsync();
    }

    public async Task<List<Role>> GetEnabledAsync()
    {
        return await _context.Roles
            .Where(r => r.Status == UserStatus.Enabled)
            .OrderBy(r => r.Sort)
            .ToListAsync();
    }

    public async Task<Role?> GetByIdAsync(int roleId)
    {
        return await _context.Roles
            .Include(r => r.RoleMenus)
            .FirstOrDefaultAsync(r => r.RoleId == roleId);
    }

    public async Task<Role?> GetByKeyAsync(string roleKey)
    {
        return await _context.Roles
            .FirstOrDefaultAsync(r => r.RoleKey == roleKey);
    }

    public async Task AddAsync(Role role)
    {
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Role role)
    {
        var existing = await _context.Roles.FindAsync(role.RoleId);
        if (existing == null) throw new InvalidOperationException("角色不存在");

        existing.RoleName = role.RoleName;
        existing.RoleKey = role.RoleKey;
        existing.Sort = role.Sort;
        existing.DataScope = role.DataScope;
        existing.Status = role.Status;
        existing.Remark = role.Remark;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int roleId)
    {
        var role = await _context.Roles.FindAsync(roleId);
        if (role == null) return;

        // 检查是否有用户使用
        var hasUsers = await _context.UserRoles.AnyAsync(ur => ur.RoleId == roleId);
        if (hasUsers)
        {
            throw new InvalidOperationException("该角色下有用户关联，无法删除");
        }

        // 删除关联的 RoleMenu
        var roleMenus = await _context.RoleMenus
            .Where(rm => rm.RoleId == roleId)
            .ToListAsync();
        _context.RoleMenus.RemoveRange(roleMenus);

        _context.Roles.Remove(role);
        await _context.SaveChangesAsync();
    }

    public async Task AssignMenusAsync(int roleId, List<int> menuIds)
    {
        // 删除旧关联
        var existingMenus = await _context.RoleMenus
            .Where(rm => rm.RoleId == roleId)
            .ToListAsync();
        _context.RoleMenus.RemoveRange(existingMenus);

        // 添加新关联
        foreach (var menuId in menuIds)
        {
            _context.RoleMenus.Add(new RoleMenu { RoleId = roleId, MenuId = menuId });
        }

        await _context.SaveChangesAsync();
    }

    public async Task<List<int>> GetRoleMenuIdsAsync(int roleId)
    {
        return await _context.RoleMenus
            .Where(rm => rm.RoleId == roleId)
            .Select(rm => rm.MenuId)
            .ToListAsync();
    }
}
