using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 菜单服务（仿若依菜单管理）
/// </summary>
public sealed class MenuService
{
    private readonly AppDbContext _context;

    public MenuService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取完整菜单树
    /// </summary>
    public async Task<List<Menu>> GetTreeAsync()
    {
        var allMenus = await _context.Menus
            .OrderBy(m => m.Sort)
            .ToListAsync();

        return BuildTree(allMenus, 0);
    }

    /// <summary>
    /// 获取所有菜单（平铺）
    /// </summary>
    public async Task<List<Menu>> GetAllAsync()
    {
        return await _context.Menus
            .OrderBy(m => m.Sort)
            .ToListAsync();
    }

    /// <summary>
    /// 获取可见菜单（用于导航）
    /// </summary>
    public async Task<List<Menu>> GetVisibleTreeAsync()
    {
        var allMenus = await _context.Menus
            .Where(m => m.Visible)
            .OrderBy(m => m.Sort)
            .ToListAsync();

        return BuildTree(allMenus, 0);
    }

    public async Task<Menu?> GetByIdAsync(int menuId)
    {
        return await _context.Menus.FindAsync(menuId);
    }

    public async Task AddAsync(Menu menu)
    {
        _context.Menus.Add(menu);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Menu menu)
    {
        var existing = await _context.Menus.FindAsync(menu.MenuId);
        if (existing == null) throw new InvalidOperationException("菜单不存在");

        existing.MenuName = menu.MenuName;
        existing.ParentId = menu.ParentId;
        existing.Sort = menu.Sort;
        existing.Path = menu.Path;
        existing.Component = menu.Component;
        existing.Type = menu.Type;
        existing.Visible = menu.Visible;
        existing.Perms = menu.Perms;
        existing.Icon = menu.Icon;
        existing.Remark = menu.Remark;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int menuId)
    {
        var menu = await _context.Menus.FindAsync(menuId);
        if (menu == null) return;

        // 检查是否有子菜单
        var hasChildren = await _context.Menus.AnyAsync(m => m.ParentId == menuId);
        if (hasChildren)
        {
            throw new InvalidOperationException("该菜单下有子菜单，无法删除");
        }

        // 删除关联的 RoleMenu
        var roleMenus = await _context.RoleMenus
            .Where(rm => rm.MenuId == menuId)
            .ToListAsync();
        _context.RoleMenus.RemoveRange(roleMenus);

        _context.Menus.Remove(menu);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 递归构建菜单树
    /// </summary>
    private static List<Menu> BuildTree(List<Menu> allMenus, int parentId)
    {
        var children = allMenus.Where(m => m.ParentId == parentId).ToList();
        foreach (var child in children)
        {
            child.Children = new List<Menu>(BuildTree(allMenus, child.MenuId));
        }
        return children;
    }
}
