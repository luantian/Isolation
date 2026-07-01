using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using System.Net;
using System.Net.Sockets;

namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 认证服务（登录验证、权限加载）
/// 安全特性：
/// - 登录失败次数计数 + 账户锁定
/// - 登录审计日志
/// - 延迟防护（防止暴力破解）
/// - BCrypt 密码哈希验证
/// </summary>
public sealed class AuthService
{
    private readonly AppDbContext _context;

    // 安全配置
    private const int MaxFailedAttempts = 5;          // 最大失败次数
    private const int LockoutMinutes = 15;             // 锁定时长（分钟）
    private const int BaseDelayMs = 200;               // 基础延迟（毫秒）
    private const int MaxDelayMs = 2000;               // 最大延迟（毫秒）

    public AuthService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 验证用户名密码并返回登录结果
    /// </summary>
    public async Task<LoginResult> LoginAsync(string userName, string password)
    {
        var clientIp = GetLocalIpAddress();
        var userAgent = Environment.MachineName;

        try
        {
            // 1. 安全延迟：无论成功失败都有延迟，防止时序攻击
            // 延迟随失败次数递增，暴力破解成本急剧上升
            var delayTask = Task.Delay(ComputeDelay(userName));

            // 2. 查询用户（忽略大小写，但密码严格匹配）
            var user = await _context.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.UserName.ToLower() == userName.ToLower());

            // 3. 用户不存在 - 记录失败日志
            if (user == null)
            {
                await delayTask;
                await LogLoginAttempt(userName, false, "用户名或密码错误", clientIp, userAgent);
                return LoginResult.Fail("用户名或密码错误");
            }

            // 4. 检查账户是否被锁定
            if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.Now)
            {
                var remaining = (int)(user.LockoutEnd.Value - DateTime.Now).TotalMinutes;
                await delayTask;
                await LogLoginAttempt(userName, false, $"账户已锁定，请 {remaining} 分钟后再试", clientIp, userAgent);
                return LoginResult.Fail($"账户已临时锁定，请 {remaining} 分钟后再试");
            }

            // 5. 检查用户是否被禁用
            if (user.Status == UserStatus.Disabled)
            {
                await delayTask;
                await LogLoginAttempt(userName, false, "用户已停用", clientIp, userAgent);
                return LoginResult.Fail("用户已停用，请联系管理员");
            }

            // 6. 验证密码（使用恒定时间比较，防止时序攻击）
            bool passwordValid = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

            if (!passwordValid)
            {
                // 密码错误：增加失败计数
                user.FailedLoginAttempts++;

                // 达到阈值时锁定账户
                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockoutEnd = DateTime.Now.AddMinutes(LockoutMinutes);
                    await LogLoginAttempt(userName, false, $"密码错误 {user.FailedLoginAttempts} 次，账户已锁定", clientIp, userAgent);
                    await _context.SaveChangesAsync();
                    await delayTask;
                    return LoginResult.Fail($"密码错误次数过多，账户已锁定 {LockoutMinutes} 分钟");
                }

                await LogLoginAttempt(userName, false, $"密码错误（第 {user.FailedLoginAttempts} 次）", clientIp, userAgent);
                await _context.SaveChangesAsync();
                await delayTask;
                return LoginResult.Fail($"用户名或密码错误（剩余尝试次数：{MaxFailedAttempts - user.FailedLoginAttempts}）");
            }

            // 7. 登录成功：重置失败计数，更新登录信息
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
            user.LastLoginTime = DateTime.Now;
            user.LoginCount++;

            // 加载权限
            var permissions = await LoadPermissionsAsync(user.UserId);

            await LogLoginAttempt(userName, true, null, clientIp, userAgent);
            await _context.SaveChangesAsync();

            await delayTask;
            return LoginResult.Success(user, permissions);
        }
        catch (Exception ex)
        {
            // 异常时也要记录日志
            await LogLoginAttempt(userName, false, $"系统异常：{ex.Message}", clientIp, userAgent);
            throw;
        }
    }

    /// <summary>
    /// 计算登录延迟（随失败次数递增）
    /// </summary>
    private int ComputeDelay(string userName)
    {
        // 即使是不存在的用户也有基础延迟
        var failedCount = _context.Users
            .Where(u => u.UserName.ToLower() == userName.ToLower())
            .Select(u => u.FailedLoginAttempts)
            .FirstOrDefault();

        // 指数退避：每次失败延迟翻倍，但不超过最大值
        var delay = BaseDelayMs * (1 << Math.Min(failedCount, 10));
        return Math.Min(delay, MaxDelayMs);
    }

    /// <summary>
    /// 记录登录审计日志
    /// </summary>
    private async Task LogLoginAttempt(string userName, bool isSuccess, string? failReason, string? clientIp, string? userAgent)
    {
        var log = new LoginLog
        {
            UserName = userName,
            IsSuccess = isSuccess,
            FailReason = failReason,
            ClientIp = clientIp,
            LoginTime = DateTime.Now,
            UserAgent = userAgent
        };
        _context.LoginLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 获取本机IP地址
    /// </summary>
    private static string? GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 加载用户的所有权限标识（基于角色 key 硬编码映射，无需手动配置）
    /// </summary>
    public async Task<HashSet<string>> LoadPermissionsAsync(int userId)
    {
        var roleKeys = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role!.RoleKey)
            .ToListAsync();

        return RolePermissions.GetPermissions(roleKeys);
    }

    /// <summary>
    /// 加载用户的角色列表
    /// </summary>
    public async Task<List<Role>> LoadRolesAsync(int userId)
    {
        var roles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role!)
            .ToListAsync();

        return roles;
    }

    /// <summary>
    /// 手动解锁账户（管理员操作）
    /// </summary>
    public async Task<bool> UnlockAccountAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return false;

        user.LockoutEnd = null;
        user.FailedLoginAttempts = 0;
        await _context.SaveChangesAsync();
        return true;
    }
}

/// <summary>
/// 登录结果
/// </summary>
public sealed class LoginResult
{
    public bool IsSuccess { get; }
    public string Error { get; } = string.Empty;
    public User? User { get; }
    public HashSet<string> Permissions { get; } = [];

    private LoginResult(bool isSuccess, string error, User? user, HashSet<string> permissions)
    {
        IsSuccess = isSuccess;
        Error = error;
        User = user;
        Permissions = permissions;
    }

    public static LoginResult Success(User user, HashSet<string> permissions) =>
        new(true, string.Empty, user, permissions);

    public static LoginResult Fail(string error) =>
        new(false, error, null, []);
}
