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

        // 业务种子数据（仅当无项目数据时插入）
        if (!await context.Projects.AnyAsync())
        {
            await SeedMasterDataAsync(context);
        }

        // 试验记录种子数据（仅当完全没有记录时插入，用于首次初始化）
        if (!await context.TestRecords.AnyAsync())
        {
            try { await SeedTestRecordsAsync(context); }
            catch { /* 忽略种子数据错误 */ }
        }

        // 安全种子数据独立判断（仅当无用户数据时插入）
        if (!await context.Users.AnyAsync())
        {
            await SeedSecurityDataAsync(context);
        }

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
        // 菜单树
        var menus = new List<Menu>
        {
            // 目录
            new() { MenuId = 1, MenuName = "首页概览", ParentId = 0, Sort = 1, Type = SysMenuType.Directory, Perms = "overview:view", Icon = "" },
            new() { MenuId = 2, MenuName = "基础台账", ParentId = 0, Sort = 2, Type = SysMenuType.Directory, Perms = "masterdata:view", Icon = "" },
            new() { MenuId = 3, MenuName = "试验记录", ParentId = 0, Sort = 3, Type = SysMenuType.Directory, Perms = "records:view", Icon = "" },
            new() { MenuId = 4, MenuName = "数据分析", ParentId = 0, Sort = 4, Type = SysMenuType.Directory, Perms = "analysis:view", Icon = "" },
            new() { MenuId = 5, MenuName = "系统设置", ParentId = 0, Sort = 5, Type = SysMenuType.Directory, Perms = "system:view", Icon = "" },

            // 基础台账子菜单
            new() { MenuId = 10, MenuName = "项目机组管理", ParentId = 2, Sort = 1, Type = SysMenuType.Menu, Perms = "masterdata:project:add", Component = "ProjectUnitManagementView" },
            new() { MenuId = 11, MenuName = "路径树管理", ParentId = 2, Sort = 2, Type = SysMenuType.Menu, Perms = "masterdata:path:add", Component = "TestObjectPathManagementView" },
            new() { MenuId = 12, MenuName = "测量装置管理", ParentId = 2, Sort = 3, Type = SysMenuType.Menu, Perms = "masterdata:device:add", Component = "MeasurementDeviceLedgerView" },

            // 试验记录子菜单
            new() { MenuId = 13, MenuName = "任务下发", ParentId = 3, Sort = 1, Type = SysMenuType.Menu, Perms = "records:task:download", Component = "" },
            new() { MenuId = 14, MenuName = "结果导入", ParentId = 3, Sort = 2, Type = SysMenuType.Menu, Perms = "records:data:upload", Component = "" },
            new() { MenuId = 15, MenuName = "报告导出", ParentId = 3, Sort = 3, Type = SysMenuType.Menu, Perms = "records:report:export", Component = "" },
            new() { MenuId = 16, MenuName = "数据删除", ParentId = 3, Sort = 4, Type = SysMenuType.Button, Perms = "records:data:delete", Component = "" },

            // 系统设置子菜单
            new() { MenuId = 20, MenuName = "用户管理", ParentId = 5, Sort = 1, Type = SysMenuType.Menu, Perms = "system:user:add", Component = "UserManagementView" },
            new() { MenuId = 21, MenuName = "角色管理", ParentId = 5, Sort = 2, Type = SysMenuType.Menu, Perms = "system:role:add", Component = "RoleManagementView" },
            new() { MenuId = 22, MenuName = "菜单管理", ParentId = 5, Sort = 3, Type = SysMenuType.Menu, Perms = "system:menu:add", Component = "MenuManagementView" },
            new() { MenuId = 23, MenuName = "操作日志", ParentId = 5, Sort = 4, Type = SysMenuType.Menu, Perms = "system:log:view", Component = "LogManagementView" },
            new() { MenuId = 24, MenuName = "数据备份", ParentId = 5, Sort = 5, Type = SysMenuType.Menu, Perms = "system:backup:view", Component = "" },
            new() { MenuId = 25, MenuName = "数据库迁移", ParentId = 5, Sort = 6, Type = SysMenuType.Menu, Perms = "system:migrate:view", Component = "" },
        };
        await context.Menus.AddRangeAsync(menus);
        await context.SaveChangesAsync();

        // 角色
        var adminRole = new Role { RoleId = 1, RoleName = "超级管理员", RoleKey = "admin", Sort = 1, DataScope = DataScope.All, Status = UserStatus.Enabled, Remark = "拥有所有权限" };
        var operatorRole = new Role { RoleId = 2, RoleName = "试验工程师", RoleKey = "operator", Sort = 2, DataScope = DataScope.Dept, Status = UserStatus.Enabled, Remark = "除系统管理外的所有权限" };
        var viewerRole = new Role { RoleId = 3, RoleName = "只读用户", RoleKey = "viewer", Sort = 3, DataScope = DataScope.All, Status = UserStatus.Enabled, Remark = "所有 view 权限" };
        await context.Roles.AddRangeAsync(adminRole, operatorRole, viewerRole);
        await context.SaveChangesAsync();

        // 角色-菜单关联
        var adminMenus = menus.Select(m => new RoleMenu { RoleId = 1, MenuId = m.MenuId }).ToList();
        await context.RoleMenus.AddRangeAsync(adminMenus);
        await context.SaveChangesAsync();

        var operatorMenus = menus.Where(m => m.ParentId != 5 && m.MenuId != 5)
            .Select(m => new RoleMenu { RoleId = 2, MenuId = m.MenuId }).ToList();
        await context.RoleMenus.AddRangeAsync(operatorMenus);
        await context.SaveChangesAsync();

        var viewMenus = menus.Where(m => m.Perms != null && m.Perms.EndsWith(":view"))
            .Select(m => new RoleMenu { RoleId = 3, MenuId = m.MenuId }).ToList();
        await context.RoleMenus.AddRangeAsync(viewMenus);
        await context.SaveChangesAsync();

        // 用户
        var adminUser = new User
        {
            UserId = 1,
            UserName = "admin",
            NickName = "系统管理员",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
            Dept = "信息中心",
            Status = UserStatus.Enabled,
            Remark = "默认超级管理员",
            CreatedAt = DateTime.Now,
            FailedLoginAttempts = 0,
            LockoutEnd = null
        };
        var demoUser = new User
        {
            UserId = 2,
            UserName = "demo",
            NickName = "演示用户",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("demo123"),
            Dept = "试验室",
            Status = UserStatus.Enabled,
            Remark = "演示用只读账户",
            CreatedAt = DateTime.Now,
            FailedLoginAttempts = 0,
            LockoutEnd = null
        };
        await context.Users.AddRangeAsync(adminUser, demoUser);
        await context.SaveChangesAsync();

        // 用户-角色关联
        await context.UserRoles.AddRangeAsync(
            new UserRole { UserId = 1, RoleId = 1 },
            new UserRole { UserId = 2, RoleId = 3 }
        );
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
}
