using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 用户会话（全局单例模式）
/// 安全特性：
/// - 会话超时自动登出
/// - 线程安全访问
/// - 管理员权限判断
/// </summary>
public sealed class UserSession
{
    private static UserSession? _instance;
    private static readonly object _lock = new();

    // 会话配置
    private const int SessionTimeoutMinutes = 30;  // 30分钟无操作自动超时

    public User User { get; private set; } = null!;
    public List<Role> Roles { get; private set; } = [];
    public HashSet<string> Permissions { get; private set; } = [];
    public DateTime LoginTime { get; private set; }
    public DateTime LastActivityTime { get; private set; }

    private UserSession(User user, List<Role> roles, HashSet<string> permissions)
    {
        User = user;
        Roles = roles;
        Permissions = permissions;
        LoginTime = DateTime.Now;
        LastActivityTime = DateTime.Now;
    }

    /// <summary>
    /// 是否已登录
    /// </summary>
    public static bool IsLoggedIn
    {
        get
        {
            lock (_lock)
            {
                if (_instance == null) return false;

                // 检查会话是否超时
                if (DateTime.Now - _instance.LastActivityTime > TimeSpan.FromMinutes(SessionTimeoutMinutes))
                {
                    _instance = null;
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// 获取当前会话（线程安全）
    /// </summary>
    public static UserSession? Current
    {
        get
        {
            lock (_lock)
            {
                if (_instance == null) return null;

                // 更新活动时间
                _instance.LastActivityTime = DateTime.Now;
                return _instance;
            }
        }
    }

    /// <summary>
    /// 登录成功时初始化会话
    /// </summary>
    public static void Initialize(User user, List<Role> roles, HashSet<string> permissions)
    {
        lock (_lock)
        {
            _instance = new UserSession(user, roles, permissions);
        }
    }

    /// <summary>
    /// 退出登录
    /// </summary>
    public static void Logout()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// 刷新会话活动时间
    /// </summary>
    public static void RefreshActivity()
    {
        lock (_lock)
        {
            if (_instance != null)
            {
                _instance.LastActivityTime = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// 检查是否有指定权限
    /// </summary>
    public static bool HasPermission(string perms)
    {
        if (!IsLoggedIn) return false;

        var session = Current;
        if (session == null) return false;

        // admin 角色拥有所有权限
        if (session.Roles.Any(r => r.RoleKey == "admin")) return true;

        return session.Permissions.Contains(perms);
    }

    /// <summary>
    /// 是否是管理员
    /// </summary>
    public static bool IsAdmin
    {
        get
        {
            if (!IsLoggedIn) return false;
            var session = Current;
            return session != null && session.Roles.Any(r => r.RoleKey == "admin");
        }
    }

    /// <summary>
    /// 当前用户显示名称
    /// </summary>
    public static string DisplayName
    {
        get
        {
            if (!IsLoggedIn) return "未登录";
            var session = Current;
            return session?.User.NickName ?? session?.User.UserName ?? "未知用户";
        }
    }

    /// <summary>
    /// 会话剩余有效时间（分钟）
    /// </summary>
    public static int? RemainingMinutes
    {
        get
        {
            lock (_lock)
            {
                if (_instance == null) return null;
                var elapsed = (DateTime.Now - _instance.LastActivityTime).TotalMinutes;
                return Math.Max(0, SessionTimeoutMinutes - (int)elapsed);
            }
        }
    }
}
