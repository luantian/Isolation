using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 用户服务（仿若依用户管理）
/// </summary>
public sealed class UserService
{
    private readonly AppDbContext _context;

    public UserService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<User>> GetAllAsync()
    {
        return await _context.Users
            .OrderBy(u => u.UserName)
            .ToListAsync();
    }

    public async Task<User?> GetByIdAsync(int userId)
    {
        return await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserId == userId);
    }

    public async Task<User?> GetByUserNameAsync(string userName)
    {
        return await _context.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.UserName == userName);
    }

    public async Task AddAsync(User user)
    {
        // UserName 有唯一索引，先在应用层查重给出友好提示，避免直接撞库抛 DbUpdateException
        if (await _context.Users.AnyAsync(u => u.UserName == user.UserName))
        {
            throw new InvalidOperationException($"用户名 {user.UserName} 已存在");
        }

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(User user)
    {
        var existing = await _context.Users.FindAsync(user.UserId);
        if (existing == null) throw new InvalidOperationException("用户不存在");

        existing.NickName = user.NickName;
        existing.Email = user.Email;
        existing.Phone = user.Phone;
        existing.Dept = user.Dept;
        existing.Status = user.Status;
        existing.Remark = user.Remark;
        existing.UpdatedAt = DateTime.Now;

        await _context.SaveChangesAsync();
    }

    public async Task UpdatePasswordAsync(int userId, string newPassword)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) throw new InvalidOperationException("用户不存在");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdatedAt = DateTime.Now;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return;

        // 删除关联的 UserRole
        var userRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .ToListAsync();
        _context.UserRoles.RemoveRange(userRoles);

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
    }

    public async Task AssignRolesAsync(int userId, List<int> roleIds)
    {
        // 删除旧关联
        var existingRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .ToListAsync();
        _context.UserRoles.RemoveRange(existingRoles);

        // 添加新关联
        foreach (var roleId in roleIds)
        {
            _context.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        }

        await _context.SaveChangesAsync();
    }

    public async Task<List<int>> GetUserRoleIdsAsync(int userId)
    {
        return await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync();
    }
}
