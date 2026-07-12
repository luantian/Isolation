using System.Data;
using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Models.Security;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 数据库初始化器
/// </summary>
public static class DatabaseInitializer
{
    private const string InitialMigrationId = "20260709063555_InitialCreate";

    /// <summary>
    /// 初始化数据库（应用迁移，插入种子数据）
    /// </summary>
    public static async Task InitializeAsync(AppDbContext context)
    {
        // 尝试应用迁移
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("already an object named") == true ||
                                           ex.Message.Contains("already an object named"))
        {
            // 旧数据库（由 EnsureCreatedAsync 创建），表已存在但无迁移历史
            // 手动标记迁移为已应用，后续 MigrateAsync 会跳过
            await MarkMigrationAsAppliedAsync(context, InitialMigrationId);
            await context.Database.MigrateAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Message.Contains("already an object named"))
        {
            await MarkMigrationAsAppliedAsync(context, InitialMigrationId);
            await context.Database.MigrateAsync();
        }

        // 安全种子数据（缺啥补啥，支持已有数据库追加）
        await SeedSecurityDataAsync(context);

        // 开发阶段：每次启动强制解锁 admin 账户，避免多次登录失败被锁定
        await UnlockAdminIfNeededAsync(context);
    }

    /// <summary>
    /// 开发阶段：强制解锁 admin 账户并重置失败次数
    /// </summary>
    private static async Task UnlockAdminIfNeededAsync(AppDbContext context)
    {
        var admin = await context.Users.FirstOrDefaultAsync(u => u.UserName == "admin");
        if (admin != null && (admin.LockoutEnd.HasValue || admin.FailedLoginAttempts > 0))
        {
            admin.LockoutEnd = null;
            admin.FailedLoginAttempts = 0;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 将迁移标记为已应用（跳过实际建表，仅记录历史）
    /// </summary>
    private static async Task MarkMigrationAsAppliedAsync(AppDbContext context, string migrationId)
    {
        var sql = $@"
            IF OBJECT_ID('__EFMigrationsHistory', 'U') IS NULL
            BEGIN
                CREATE TABLE __EFMigrationsHistory (
                    MigrationId NVARCHAR(150) NOT NULL PRIMARY KEY,
                    ProductVersion NVARCHAR(32) NOT NULL
                );
            END
            IF NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory WHERE MigrationId = '{migrationId}')
            BEGIN
                INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('{migrationId}', '8.0.10');
            END";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>
    /// 插入基础数据
    /// </summary>

    private static async Task SeedMasterDataAsync(AppDbContext context)
    {
        // 项目
        var projects = new[]
        {
            new Project
            {
                Code = "HN",
                Name = "海南项目",
                Status = EnabledStatus.Enabled,
                Remark = "示例项目"
            },
            new Project
            {
                Code = "ZZ",
                Name = "漳州项目",
                Status = EnabledStatus.Enabled,
                Remark = "示例项目"
            }
        };
        await context.Projects.AddRangeAsync(projects);
        await context.SaveChangesAsync();

        // 机组
        var units = new[]
        {
            new Unit
            {
                Code = "HN-3",
                Name = "海南 3 号机组",
                ProjectCode = "HN",
                Status = EnabledStatus.Enabled,
                Remark = "示例机组"
            },
            new Unit
            {
                Code = "HN-4",
                Name = "海南 4 号机组",
                ProjectCode = "HN",
                Status = EnabledStatus.Enabled,
                Remark = "示例机组"
            },
            new Unit
            {
                Code = "ZZ-1",
                Name = "漳州 1 号机组",
                ProjectCode = "ZZ",
                Status = EnabledStatus.Enabled,
                Remark = "示例机组"
            }
        };
        await context.Units.AddRangeAsync(units);
        await context.SaveChangesAsync();


        // 测量装置
        var devices = new[]
        {
            new MeasurementDevice
            {
                DeviceCode = "DEV-001",
                DeviceName = "泄漏率测量装置 01",
                Ip = "192.168.1.101",
                SerialNumber = "SN-HN-0001",
                PrimaryCommunication = CommunicationType.Usb,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Online,
                LastSyncTime = new DateTime(2026, 5, 26, 12, 18, 0),
                LastUploadTime = new DateTime(2026, 5, 26, 12, 18, 0),
                UploadCount = 128,
                LastUploadResult = TestResult.Pass,
                Remark = "示例装置"
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-002",
                DeviceName = "泄漏率测量装置 02",
                Ip = "192.168.1.102",
                SerialNumber = "SN-HN-0002",
                PrimaryCommunication = CommunicationType.Rj45,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Online,
                LastSyncTime = new DateTime(2026, 5, 26, 11, 42, 0),
                LastUploadTime = new DateTime(2026, 5, 26, 11, 42, 0),
                UploadCount = 96,
                LastUploadResult = TestResult.Pass,
                Remark = "示例装置"
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-003",
                DeviceName = "泄漏率测量装置 03",
                Ip = "192.168.1.103",
                SerialNumber = "SN-HN-0003",
                PrimaryCommunication = CommunicationType.Rs232,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Offline,
                LastSyncTime = new DateTime(2026, 5, 25, 16, 5, 0),
                LastUploadTime = new DateTime(2026, 5, 25, 16, 5, 0),
                UploadCount = 62,
                LastUploadResult = TestResult.Fail,
                Remark = "示例装置"
            }
        };
        await context.MeasurementDevices.AddRangeAsync(devices);
        await context.SaveChangesAsync();

        // ================ 安全种子数据（仿若依） ================
        await SeedSecurityDataAsync(context);
    }

    /// <summary>
    /// 插入安全种子数据（用户、角色、菜单）
    /// </summary>
    private static async Task SeedSecurityDataAsync(AppDbContext context)
    {
        // ── 菜单（按 MenuId 逐个检查，缺的才补） ──
        var menuSeed = new List<Menu>
        {
            new() { MenuId = 1, MenuName = "首页概览", ParentId = 0, Sort = 1, Type = SysMenuType.Directory, Perms = "overview:view", Icon = "" },
            new() { MenuId = 2, MenuName = "基础台账", ParentId = 0, Sort = 2, Type = SysMenuType.Directory, Perms = "masterdata:view", Icon = "" },
            new() { MenuId = 3, MenuName = "试验记录", ParentId = 0, Sort = 3, Type = SysMenuType.Directory, Perms = "records:view", Icon = "" },
            new() { MenuId = 4, MenuName = "数据分析", ParentId = 0, Sort = 4, Type = SysMenuType.Directory, Perms = "analysis:view", Icon = "" },
            new() { MenuId = 5, MenuName = "系统设置", ParentId = 0, Sort = 5, Type = SysMenuType.Directory, Perms = "system:view", Icon = "" },
            new() { MenuId = 10, MenuName = "项目机组管理", ParentId = 2, Sort = 1, Type = SysMenuType.Menu, Perms = "masterdata:project:add", Component = "ProjectUnitManagementView" },
            new() { MenuId = 11, MenuName = "路径树管理", ParentId = 2, Sort = 2, Type = SysMenuType.Menu, Perms = "masterdata:path:add", Component = "TestObjectPathManagementView" },
            new() { MenuId = 12, MenuName = "测量装置管理", ParentId = 2, Sort = 3, Type = SysMenuType.Menu, Perms = "masterdata:device:add", Component = "MeasurementDeviceLedgerView" },
            new() { MenuId = 13, MenuName = "任务下发", ParentId = 3, Sort = 1, Type = SysMenuType.Menu, Perms = "records:task:download", Component = "" },
            new() { MenuId = 14, MenuName = "结果导入", ParentId = 3, Sort = 2, Type = SysMenuType.Menu, Perms = "records:data:upload", Component = "" },
            new() { MenuId = 15, MenuName = "报告导出", ParentId = 3, Sort = 3, Type = SysMenuType.Menu, Perms = "records:report:export", Component = "" },
            new() { MenuId = 16, MenuName = "数据删除", ParentId = 3, Sort = 4, Type = SysMenuType.Button, Perms = "records:data:delete", Component = "" },
            new() { MenuId = 20, MenuName = "用户管理", ParentId = 5, Sort = 1, Type = SysMenuType.Menu, Perms = "system:user:add", Component = "UserManagementView" },
            new() { MenuId = 21, MenuName = "角色管理", ParentId = 5, Sort = 2, Type = SysMenuType.Menu, Perms = "system:role:add", Component = "RoleManagementView" },
            new() { MenuId = 23, MenuName = "操作日志", ParentId = 5, Sort = 3, Type = SysMenuType.Menu, Perms = "system:log:view", Component = "LogManagementView" },
            new() { MenuId = 24, MenuName = "数据备份", ParentId = 5, Sort = 4, Type = SysMenuType.Menu, Perms = "system:backup:view", Component = "" },
            new() { MenuId = 25, MenuName = "数据库迁移", ParentId = 5, Sort = 5, Type = SysMenuType.Menu, Perms = "system:migrate:view", Component = "" },
        };
        var existingMenuIds = await context.Menus.Select(m => m.MenuId).ToListAsync();
        var newMenus = menuSeed.Where(m => !existingMenuIds.Contains(m.MenuId)).ToList();
        if (newMenus.Count > 0)
        {
            // MenuId 是 IDENTITY 列，需要临时开启 IDENTITY_INSERT 才能显式赋值
            // 注意：IDENTITY_INSERT 是连接级别的，必须显式打开连接确保同一会话
            var conn = context.Database.GetDbConnection();
            await conn.OpenAsync();

            // 临时禁用外键检查（ParentId=0 的顶级菜单会触发自引用外键冲突）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SET IDENTITY_INSERT [Menus] ON; ALTER TABLE [Menus] NOCHECK CONSTRAINT ALL;";
                await cmd.ExecuteNonQueryAsync();
            }
            try
            {
                await context.Menus.AddRangeAsync(newMenus);
                await context.SaveChangesAsync();
            }
            finally
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE [Menus] CHECK CONSTRAINT ALL; SET IDENTITY_INSERT [Menus] OFF;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── 角色（按 RoleKey 检查，缺的才补） ──
        var roleSeed = new (int Id, string Name, string Key, string Remark)[]
        {
            (1, "超级管理员", "admin", "全部权限，可编辑/删除/管理系统"),
            (2, "试验工程师", "operator", "可编辑数据、导出报告，不可删除"),
            (3, "只读用户", "viewer", "只能查看和导出报告"),
        };
        var existingRoleKeys = await context.Roles.Select(r => r.RoleKey).ToListAsync();
        var rolesToAdd = new List<Role>();
        foreach (var (id, name, key, remark) in roleSeed)
        {
            if (!existingRoleKeys.Contains(key))
            {
                rolesToAdd.Add(new Role
                {
                    RoleId = id,
                    RoleName = name,
                    RoleKey = key,
                    Sort = id,
                    DataScope = key == "operator" ? DataScope.Dept : DataScope.All,
                    Status = UserStatus.Enabled,
                    Remark = remark,
                });
            }
        }
        if (rolesToAdd.Count > 0)
        {
            var conn = context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SET IDENTITY_INSERT [Roles] ON";
                await cmd.ExecuteNonQueryAsync();
            }
            try
            {
                await context.Roles.AddRangeAsync(rolesToAdd);
                await context.SaveChangesAsync();
            }
            finally
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SET IDENTITY_INSERT [Roles] OFF";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── 用户（按 UserName 检查，缺的才补） ──
        var userSeed = new (string UserName, string NickName, string Password, string Dept, string Remark)[]
        {
            ("admin", "系统管理员", "admin123", "信息中心", "默认超级管理员"),
            ("operator", "试验工程师", "operator123", "试验室", "默认试验工程师账户"),
            ("viewer", "只读用户", "viewer123", "管理层", "默认只读账户"),
        };
        var existingUserNames = await context.Users.Select(u => u.UserName).ToListAsync();
        var newUsers = new List<User>();
        int nextUserId = await context.Users.AnyAsync()
            ? await context.Users.MaxAsync(u => u.UserId) + 1
            : 1;
        foreach (var (userName, nickName, password, dept, remark) in userSeed)
        {
            if (!existingUserNames.Contains(userName))
            {
                newUsers.Add(new User
                {
                    UserId = nextUserId++,
                    UserName = userName,
                    NickName = nickName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    Dept = dept,
                    Status = UserStatus.Enabled,
                    Remark = remark,
                    CreatedAt = DateTime.Now,
                    FailedLoginAttempts = 0,
                    LockoutEnd = null,
                });
            }
        }
        if (newUsers.Count > 0)
        {
            var conn = context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SET IDENTITY_INSERT [Users] ON";
                await cmd.ExecuteNonQueryAsync();
            }
            try
            {
                await context.Users.AddRangeAsync(newUsers);
                await context.SaveChangesAsync();
            }
            finally
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SET IDENTITY_INSERT [Users] OFF";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── 用户-角色关联（按 UserName→RoleKey 检查，缺的才补） ──
        var userRoleMap = new Dictionary<string, string>
        {
            ["admin"] = "admin",
            ["operator"] = "operator",
            ["viewer"] = "viewer",
        };
        var allUsers = await context.Users.ToListAsync();
        var allRoles = await context.Roles.ToListAsync();
        var existingUserRoles = await context.UserRoles.ToListAsync();

        foreach (var (userName, roleKey) in userRoleMap)
        {
            var user = allUsers.FirstOrDefault(u => u.UserName == userName);
            var role = allRoles.FirstOrDefault(r => r.RoleKey == roleKey);
            if (user == null || role == null) continue;
            if (!existingUserRoles.Any(ur => ur.UserId == user.UserId && ur.RoleId == role.RoleId))
            {
                await context.UserRoles.AddAsync(new UserRole { UserId = user.UserId, RoleId = role.RoleId });
            }
        }
        await context.SaveChangesAsync();
    }
}