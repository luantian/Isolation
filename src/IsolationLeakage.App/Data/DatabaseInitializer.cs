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
    private const string InitialMigrationId = "20260609111405_InitialCreate";

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

        // 测量装置种子数据（仅当无装置时插入）——导入数据需要装置编码，独立于项目台账
        if (!await context.MeasurementDevices.AnyAsync())
        {
            await SeedDevicesAsync(context);
        }

        // 配方种子数据（仅当无配方时插入）
        if (!await context.TestRecipes.AnyAsync())
        {
            await SeedTestRecipesAsync(context);
        }

        // 注：不再自动灌入示例项目/机组/路径节点和示例试验记录。
        // 项目台账与试验数据完全由用户通过批量导入建立，保持演示库干净。

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
    /// <summary>
    /// 插入测量装置种子数据（独立于项目台账，导入数据需要装置编码 DEV-001/002/003）
    /// </summary>
    private static async Task SeedDevicesAsync(AppDbContext context)
    {
        var devices = new[]
        {
            new MeasurementDevice
            {
                DeviceCode = "DEV-001",
                DeviceName = "泄漏率测量装置 01",
                Model = "LRM-100",
                SerialNumber = "SN-HN-0001",
                PrimaryCommunication = CommunicationType.Usb,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Offline,
                Remark = "示例装置"
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-002",
                DeviceName = "泄漏率测量装置 02",
                Model = "LRM-100",
                SerialNumber = "SN-HN-0002",
                PrimaryCommunication = CommunicationType.Rj45,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Offline,
                Remark = "示例装置"
            },
            new MeasurementDevice
            {
                DeviceCode = "DEV-003",
                DeviceName = "泄漏率测量装置 03",
                Model = "LRM-200",
                SerialNumber = "SN-HN-0003",
                PrimaryCommunication = CommunicationType.Rs232,
                EnabledStatus = EnabledStatus.Enabled,
                ConnectionStatus = ConnectionStatus.Offline,
                Remark = "示例装置"
            }
        };
        await context.MeasurementDevices.AddRangeAsync(devices);
        await context.SaveChangesAsync();
    }

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

        // 试验对象路径树（海南 3 号机组）
        var rhrSystem = new TestObjectPathNode
        {
            Code = "RHR",
            Name = "余热排出系统",
            NodeType = PathNodeType.System,
            UnitCode = "HN-3",
            ParentCode = null,
            Remark = "海南 3 号机组示例系统路径"
        };
        await context.TestObjectPathNodes.AddAsync(rhrSystem);
        await context.SaveChangesAsync();

        var penetration = new TestObjectPathNode
        {
            Code = "IPNI01",
            Name = "贯穿件路径",
            NodeType = PathNodeType.Penetration,
            UnitCode = "HN-3",
            ParentCode = "RHR",
            LeakageLimit = 0.08m,
            Remark = "贯穿件可继续挂载阀门或其他密封性部件"
        };
        await context.TestObjectPathNodes.AddAsync(penetration);
        await context.SaveChangesAsync();

        var valves = new[]
        {
            new TestObjectPathNode
            {
                Code = "1RHR040VP",
                Name = "隔离阀",
                NodeType = PathNodeType.Valve,
                UnitCode = "HN-3",
                ParentCode = "IPNI01",
                ValveType = "电动阀",
                LeakageLimit = 0.05m,
                TestPressure = 0.9m,
                Remark = "贯穿件下的阀门路径"
            },
            new TestObjectPathNode
            {
                Code = "1RHR041VP",
                Name = "隔离阀",
                NodeType = PathNodeType.Valve,
                UnitCode = "HN-3",
                ParentCode = "IPNI01",
                ValveType = "止回阀",
                LeakageLimit = 0.05m,
                TestPressure = 0.9m,
                Remark = "贯穿件下的阀门路径"
            }
        };
        await context.TestObjectPathNodes.AddRangeAsync(valves);
        await context.SaveChangesAsync();

        var sealComponent = new TestObjectPathNode
        {
            Code = "RHR-SEAL-01",
            Name = "密封性部件",
            NodeType = PathNodeType.OtherComponent,
            UnitCode = "HN-3",
            ParentCode = "RHR",
            ComponentType = "密封型",
            LeakageLimit = 0.06m,
            TestPressure = 0.8m,
            Remark = "系统下直接建立的其他密封性部件路径"
        };
        await context.TestObjectPathNodes.AddAsync(sealComponent);
        await context.SaveChangesAsync();

        // 测量装置
        var devices = new[]
        {
            new MeasurementDevice
            {
                DeviceCode = "DEV-001",
                DeviceName = "泄漏率测量装置 01",
                Model = "LRM-100",
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
                Model = "LRM-100",
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
                Model = "LRM-200",
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
    /// 插入试验记录种子数据（不足 50 条时补充，用于分页测试）
    /// </summary>
    private static async Task SeedTestRecordsAsync(AppDbContext context)
    {
        var existingCount = await context.TestRecords.CountAsync();
        if (existingCount >= 50) return;

        var rnd = new Random(42);
        var testRecords = new List<TestRecord>();

        var objectCodes = new[] { "1RHR040VP", "1RHR041VP", "RHR-SEAL-01" };
        var objectNames = new[] { "隔离阀", "隔离阀", "密封性部件" };
        var objectTypes = new[] { PathNodeType.Valve, PathNodeType.Valve, PathNodeType.OtherComponent };
        var deviceCodes = new[] { "DEV-001", "DEV-002", "DEV-003" };
        var pressures = new[] { 0.8m, 0.85m, 0.9m, 0.95m, 1.0m };
        var limits = new[] { 0.05m, 0.06m, 0.08m };
        var remarks = new[] { "定期检测", "大修后检测", "日常巡检", "专项检查", "抽检" };

        var baseDate = new DateTime(2026, 5, 25, 8, 0, 0);
        for (int i = existingCount; i < 50; i++)
        {
            var objIdx = i % 3;
            var device = deviceCodes[i % 3];
            var pressure = pressures[i % pressures.Length];
            var limit = limits[i % limits.Length];
            var isPass = rnd.Next(100) < 80;
            var rate = isPass
                ? Math.Round(limit * (decimal)(0.2 + rnd.NextDouble() * 0.7), 3)
                : Math.Round(limit * (decimal)(1.1 + rnd.NextDouble() * 0.8), 3);
            var testTime = baseDate.AddMinutes(i * 45 + rnd.Next(0, 30));

            var record = CreateSampleTestRecord(
                $"TR-202605{i / 20 + 25:D2}{i % 20 + 1:D2}-{i + 1:D3}",
                "HN", "HN-3",
                objectCodes[objIdx], objectNames[objIdx], objectTypes[objIdx],
                device, testTime,
                pressure, limit, rate,
                isPass ? TestResult.Pass : TestResult.Fail,
                rnd);
            record.Remark = remarks[i % remarks.Length];
            testRecords.Add(record);
        }

        await context.TestRecords.AddRangeAsync(testRecords);
        await context.SaveChangesAsync();
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SET IDENTITY_INSERT [Menus] ON";
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
                cmd.CommandText = "SET IDENTITY_INSERT [Menus] OFF";
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

    private static TestRecord CreateSampleTestRecord(
        string code, string projectCode, string unitCode, string objCode, string objName,
        PathNodeType objType, string deviceCode, DateTime testTime,
        decimal pressure, decimal limit, decimal rate, TestResult result, Random rnd)
    {
        var processData = GenerateProcessCurveData((double)pressure, (double)rate, rnd);

        return new TestRecord
        {
            RecordCode = code,
            ProjectCode = projectCode,
            UnitCode = unitCode,
            ObjectCode = objCode,
            ObjectName = objName,
            ObjectType = objType,
            DeviceCode = deviceCode,
            DataPackageName = $"PKG_{objCode}_{testTime:yyyyMMdd_HHmm}.dat",
            TestTime = testTime,
            ImportTime = testTime.AddMinutes(6),
            Operator = "admin",
            TestPressure = pressure,
            LeakageLimit = limit,
            FinalLeakageRate = rate,
            Result = result,
            Remark = "示例记录",
            StepSummary = "建压 -> 稳压 -> 采集 -> 判定 -> U 盘拷贝 -> 结果导入",
            ResultFieldSummary = "试验压力、泄漏限值、最终泄漏率、判定结果、试验时间",
            ProcessChannelSummary = "CSV 过程采集数据，15 个通道，按时间轴回放",
            ProcessData = processData
        };
    }

    private static TestProcessData GenerateProcessCurveData(double basePressure, double baseFlow, Random rnd)
    {
        const int n = 200;
        var pressureData = new double[n];
        var flowData = new double[n];
        var tempData = new double[n];

        double baseTemp = 24.0 + rnd.NextDouble() * 1.5;

        double pMin = double.MaxValue, pMax = double.MinValue;
        double fMin = double.MaxValue, fMax = double.MinValue;
        double tMin = double.MaxValue, tMax = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)n;
            double p, f, tp;

            if (t < 0.15)
            {
                double phase = t / 0.15;
                p = basePressure * (1 - Math.Exp(-phase * 4)) + rnd.NextDouble() * 0.02;
                f = baseFlow * (2 + rnd.NextDouble()) * (1 - phase) + rnd.NextDouble() * 0.005;
                tp = baseTemp - 0.3 + rnd.NextDouble() * 0.2;
            }
            else if (t < 0.3)
            {
                double phase = (t - 0.15) / 0.15;
                p = basePressure * (1.05 - 0.05 * phase) + (rnd.NextDouble() - 0.5) * 0.01;
                f = baseFlow * (1.5 + 0.5 * Math.Sin(phase * 10) * (1 - phase)) + (rnd.NextDouble() - 0.5) * 0.003;
                tp = baseTemp + 0.2 * phase + (rnd.NextDouble() - 0.5) * 0.15;
            }
            else
            {
                double phase = (t - 0.3) / 0.7;
                p = basePressure + (rnd.NextDouble() - 0.5) * 0.008 - phase * 0.01;
                f = baseFlow + 0.003 * Math.Sin(phase * 20) + (rnd.NextDouble() - 0.5) * 0.002;
                tp = baseTemp + 0.3 + 0.1 * Math.Sin(phase * 5) + (rnd.NextDouble() - 0.5) * 0.1;
            }

            p = Math.Max(0, p);
            f = Math.Max(0, f);

            pressureData[i] = p;
            flowData[i] = f;
            tempData[i] = tp;

            pMin = Math.Min(pMin, p); pMax = Math.Max(pMax, p);
            fMin = Math.Min(fMin, f); fMax = Math.Max(fMax, f);
            tMin = Math.Min(tMin, tp); tMax = Math.Max(tMax, tp);
        }

        static double expand(double v, double range, bool up) => up ? v + range * 0.05 : v - range * 0.05;
        double pRange = pMax - pMin; if (pRange == 0) pRange = 0.1;
        double fRange = fMax - fMin; if (fRange == 0) fRange = 0.001;
        double tRange = tMax - tMin; if (tRange == 0) tRange = 0.5;

        var pressureJson = System.Text.Json.JsonSerializer.Serialize(pressureData);
        var flowJson = System.Text.Json.JsonSerializer.Serialize(flowData);
        var tempJson = System.Text.Json.JsonSerializer.Serialize(tempData);

        return new TestProcessData
        {
            PressureCurveJson = pressureJson,
            FlowCurveJson = flowJson,
            TempCurveJson = tempJson,
            PressureMin = (decimal)expand(pMin, pRange, false),
            PressureMax = (decimal)expand(pMax, pRange, true),
            FlowMin = (decimal)expand(fMin, fRange, false),
            FlowMax = (decimal)expand(fMax, fRange, true),
            TempMin = (decimal)expand(tMin, tRange, false),
            TempMax = (decimal)expand(tMax, tRange, true)
        };
    }

    /// <summary>
    /// 插入试验配方种子数据
    /// </summary>
    private static async Task SeedTestRecipesAsync(AppDbContext context)
    {
        var defaultRecipes = new List<TestRecipe>
        {
            new()
            {
                RecipeCode = "A",
                RecipeName = "配方A - 低压标准",
                Description = "适用于低压密封试验",
                AirtightTargetPressureP1 = 1,
                AirtightAllowDropValue = 0,
                FineBlowTargetPressureP1 = 6,
                PurgeReleasePressure = 0,
                NormalExpectedLeakFlow = 0,
                SmallPrechargeTargetP1 = 0,
                SmallPrechargeTargetP2 = 0,
                MediumPrechargeTargetP1 = 0,
                MediumPrechargeTargetP2 = 0,
                LargePrechargeTargetP1 = 0,
                LargePrechargeTargetP2 = 0,
                IsEnabled = true,
                SortOrder = 1,
                CreatedBy = "system"
            },
            new()
            {
                RecipeCode = "B",
                RecipeName = "配方B - 中压标准",
                Description = "适用于中压密封试验",
                AirtightTargetPressureP1 = 5,
                AirtightAllowDropValue = 2,
                FineBlowTargetPressureP1 = 6,
                PurgeReleasePressure = 0,
                NormalExpectedLeakFlow = 0,
                SmallPrechargeTargetP1 = 0,
                SmallPrechargeTargetP2 = 0,
                MediumPrechargeTargetP1 = 0,
                MediumPrechargeTargetP2 = 0,
                LargePrechargeTargetP1 = 0,
                LargePrechargeTargetP2 = 0,
                IsEnabled = true,
                SortOrder = 2,
                CreatedBy = "system"
            },
            new()
            {
                RecipeCode = "C",
                RecipeName = "配方C - 高压精吹",
                Description = "适用于高压精吹试验",
                AirtightTargetPressureP1 = 5,
                AirtightAllowDropValue = 0,
                FineBlowTargetPressureP1 = 3,
                PurgeReleasePressure = 0,
                NormalExpectedLeakFlow = 0,
                SmallPrechargeTargetP1 = 0,
                SmallPrechargeTargetP2 = 0,
                MediumPrechargeTargetP1 = 0,
                MediumPrechargeTargetP2 = 0,
                LargePrechargeTargetP1 = 0,
                LargePrechargeTargetP2 = 0,
                IsEnabled = true,
                SortOrder = 3,
                CreatedBy = "system"
            }
        };

        context.TestRecipes.AddRange(defaultRecipes);
        await context.SaveChangesAsync();
    }
}
