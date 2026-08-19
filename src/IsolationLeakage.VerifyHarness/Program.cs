using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.VerifyHarness;

/// <summary>
/// 无头验证 harness：在 STA 线程上直接驱动 RealtimeMonitorViewModel 的真实命令链路
/// （Mock PLC 连接 → 开始监视 → 停止监视），验证 2026-08-18 的三处修复：
///   P1: 实时监视记录绑定默认配方（TestRecipeId + RecipeSnapshotJson + 判定限值=配方限值）
///   P3: 监视中切换项目/机组/试验对象被拦截并回弹
///   P4: 开始监视不再把变量表格未保存的编辑静默回滚
/// 使用本机 .\SQLEXPRESS 独立验证库 IsolationLeakageVerifyDb（每次运行重建）。
/// </summary>
internal static class Program
{
    private const string ConnString =
        @"Server=.\SQLEXPRESS;Database=IsolationLeakageVerifyDb;User Id=sa;Password=123456;Trust Server Certificate=True;Connect Timeout=15";

    private static readonly List<string> Failures = new();

    // 测试用单装置 plc-registers.json：Modbus + 仿真降级，地址对应 MockPlcConnection 的已知地址
    // （512=压力P1, 504=压力P2, 500=温度T, 804=流量M1, 806=流量M2）
    private const string TestPlcJson = """
    {
      "PlcRegisters": {
        "SampleIntervalMs": 500,
        "Devices": [
          {
            "DeviceCode": "DEFAULT",
            "Connection": {
              "PlcType": "Modbus",
              "IpAddress": "127.0.0.1",
              "Port": 502,
              "Protocol": "tcp",
              "AllowSimulationFallback": true
            },
            "SampleIntervalMs": 500,
            "Variables": [
              { "VariableCode": "PLC_PRESSURE_P1", "VariableName": "压力P1", "RegisterAddress": 512, "DataType": "double", "Unit": "MPa", "CurveChannel": "Pressure", "MinDisplay": 0, "MaxDisplay": 10000 },
              { "VariableCode": "PLC_TEMP", "VariableName": "温度T", "RegisterAddress": 500, "DataType": "double", "Unit": "℃", "CurveChannel": "Temp", "MinDisplay": -20, "MaxDisplay": 100 },
              { "VariableCode": "PLC_FLOW_M1", "VariableName": "流量M1", "RegisterAddress": 804, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow", "MinDisplay": 0, "MaxDisplay": 20000 },
              { "VariableCode": "PLC_FLOW_M2", "VariableName": "流量M2", "RegisterAddress": 806, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow2", "MinDisplay": 0, "MaxDisplay": 20000 }
            ]
          }
        ]
      }
    }
    """;

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 控制台编码不可设置时忽略 */ }

        if (args.Contains("csv", StringComparer.OrdinalIgnoreCase))
        {
            bool csvOk;
            try { csvOk = RunCsvChecksAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] csv 检查异常: " + ex);
                csvOk = false;
            }
            Console.WriteLine(csvOk ? "== CSV ALL PASS ==" : "== CSV FAILED ==");
            return csvOk ? 0 : 1;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        bool ok = false;

        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                ok = args.Contains("switch", StringComparer.OrdinalIgnoreCase)
                    ? await RunSwitchVerificationAsync()
                    : await RunVerificationAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] harness 异常: " + ex);
                ok = false;
            }
            finally { frame.Continue = false; }
        }));

        Dispatcher.PushFrame(frame);

        Console.WriteLine(ok ? "== ALL PASS ==" : "== FAILED ==");
        return ok ? 0 : 1;
    }

    private static void Check(bool cond, string name, string detail = "")
    {
        Console.WriteLine($"{(cond ? "[PASS]" : "[FAIL]")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!cond) Failures.Add(name);
    }

    private static async Task<bool> RunVerificationAsync()
    {
        Console.WriteLine("== 准备数据库（每次运行重建 IsolationLeakageVerifyDb）==");

        DbContextFactory.Configure(ConnString);
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.MigrateAsync();
        }

        AppServices.Initialize(DbContextFactory.CreateDbContext());

        // ---- 种子数据 ----
        const string projCode = "VP01", unitCode = "VU01", nodeCode = "V-VRFY-01", devCode = "VERIFY-PLC";
        const decimal recipeLimit = 10m, nodeLimit = 999999m;   // 相差悬殊，用于区分限值来源

        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.Projects.Add(new Project { Code = projCode, Name = "验证项目", Status = EnabledStatus.Enabled });
            ctx.Units.Add(new Unit { Code = unitCode, Name = "验证机组", ProjectCode = projCode, Status = EnabledStatus.Enabled });
            ctx.MeasurementDevices.Add(new MeasurementDevice
            {
                DeviceCode = devCode,
                DeviceName = "验证装置",
                Ip = "127.0.0.1",
                EnabledStatus = EnabledStatus.Enabled,
            });
            // 单装置模式的变量配置（P4：json 4 个变量 → DB 3 个，若构造时从 DB 加载则表格=3）
            ctx.Set<MonitorVariableConfig>().AddRange(
                new MonitorVariableConfig { VariableName = "压力P1", RegisterAddress = 512, DataType = "double", Unit = "MPa", CurveChannel = "Pressure", MinDisplay = 0, MaxDisplay = 5000, SortOrder = 1 },
                new MonitorVariableConfig { VariableName = "流量M1", RegisterAddress = 804, DataType = "uint", Unit = "Nml/min", CurveChannel = "Flow", MinDisplay = 0, MaxDisplay = 20000, SortOrder = 2 },
                new MonitorVariableConfig { VariableName = "流量M2", RegisterAddress = 806, DataType = "uint", Unit = "Nml/min", CurveChannel = "Flow2", MinDisplay = 0, MaxDisplay = 20000, SortOrder = 3 });
            await ctx.SaveChangesAsync();
        }

        var recipe = await AppServices.RecipeService.CreateAsync(new TestRecipe
        {
            RecipeName = "验证配方", SequenceNo = 1, System = "验证系统",
            LeakageLimit = recipeLimit, PrechargePressureP2 = 600, IsEnabled = true,
        }, "验证种子", "verify");

        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.TestObjectPathNodes.Add(new TestObjectPathNode
            {
                Code = nodeCode, Name = "验证阀门", NodeType = PathNodeType.Valve,
                UnitCode = unitCode, ParentCode = null, ValveType = "闸阀",
                LeakageLimit = nodeLimit, TestPressure = 1.5m,
                DefaultRecipeId = recipe.Id, Status = EnabledStatus.Enabled,
            });
            await ctx.SaveChangesAsync();
        }

        // 覆盖输出目录的 plc-registers.json（仅本 harness 的 bin，不动源码）
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plc-registers.json"), TestPlcJson);

        Console.WriteLine("== 创建 ViewModel 并等待初始加载 ==");

        // 用旧连接串再确认 AppServices 持有的 DbContext 已就绪后创建 VM（构造内部会触发 DB 加载）
        var vm = new RealtimeMonitorViewModel();

        // 等待项目/机组/对象/装置四组数据加载完成
        Assert.True(await WaitForAsync(() => vm.AvailableProjects.Any(p => p.Code == projCode), 15000), "项目列表加载");
        vm.SelectedProject = vm.AvailableProjects.First(p => p.Code == projCode);
        Assert.True(await WaitForAsync(() => vm.AvailableUnits.Any(u => u.Code == unitCode), 10000), "机组列表加载");
        vm.SelectedUnit = vm.AvailableUnits.First(u => u.Code == unitCode);
        Assert.True(await WaitForAsync(() => vm.AvailableObjects.Any(o => o.Code == nodeCode), 10000), "试验对象列表加载");
        vm.SelectedObject = vm.AvailableObjects.First(o => o.Code == nodeCode);
        Assert.True(await WaitForAsync(() => vm.AvailableDevices.Any(d => d.DeviceCode == devCode), 10000), "台账装置加载");
        vm.SelectedDevice = vm.AvailableDevices.First(d => d.DeviceCode == devCode);

        // ---- P4 前置：构造时已从 DB 加载变量（json=4 → DB=3，等表格变成 3 说明 DB 配置生效）----
        Assert.True(await WaitForAsync(() => vm.MonitorVariables.Count == 3, 10000), "P4 前置: 构造时从DB加载变量配置(3条)");
        vm.MonitorVariables[0].MaxDisplay = 123.45;   // 模拟用户未保存的表格编辑
        Console.WriteLine($"    已做未保存编辑: MaxDisplay={vm.MonitorVariables[0].MaxDisplay}");

        // ---- 连接 PLC（Modbus 真连失败 → AllowSimulationFallback → Mock）----
        await ((IAsyncRelayCommand)vm.ConnectPlcCommand).ExecuteAsync(null);
        Assert.True(await WaitForAsync(() => vm.IsConnected, 20000), $"连接PLC(仿真降级) — 状态: {vm.ConnectionState}");

        // ---- 开始监视 ----
        await ((IAsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);
        Assert.True(vm.IsMonitoring, "开始监视");

        // ---- P4 验证：启动后未保存编辑仍在（修复前会被 DB 重载清掉）----
        await Task.Delay(1500);
        Check(vm.MonitorVariables.Count == 3 && Math.Abs(vm.MonitorVariables[0].MaxDisplay - 123.45) < 0.001,
            "P4: 开始监视后未保存的变量编辑保留", $"数量={vm.MonitorVariables.Count}, MaxDisplay={vm.MonitorVariables[0].MaxDisplay}");

        // ---- P3 验证：监视中改项目下拉 → 弹窗拦截 + 回弹 ----
        var lockedProject = vm.SelectedProject;
        var clicker = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++) { TryClickOkDialog("提示"); await Task.Delay(50); }
        });
        vm.SelectedProject = null;   // 应被 GuardMonitoringSelection 拦截并回弹
        await clicker;
        Check(ReferenceEquals(vm.SelectedProject, lockedProject),
            "P3: 监视中切换项目被拦截并回弹", $"当前={vm.SelectedProject?.Code ?? "null"}");

        // ---- 采集 ~6 秒（Mock 流量基线 ~25 Nml/min > 配方限值 10 → 应判 Fail）----
        Console.WriteLine("    采集中（约6秒）...");
        await Task.Delay(6000);
        Assert.True(await WaitForAsync(() => vm.TimeAxisPoints.Count >= 5, 10000), "曲线采样点累积");

        // ---- 停止监视（内部完成最终持久化与判定）----
        await ((IAsyncRelayCommand)vm.StopMonitoringCommand).ExecuteAsync(null);
        Assert.True(!vm.IsMonitoring, "停止监视");

        // ---- P1 验证：查库核对记录 ----
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            var record = await ctx.TestRecords
                .Where(r => r.ObjectCode == nodeCode)
                .OrderByDescending(r => r.CreatedAt)
                .FirstAsync();

            Check(record.TestRecipeId == recipe.Id, "P1: TestRecipeId 绑定默认配方", $"实际={record.TestRecipeId}, 期望={recipe.Id}");
            Check(!string.IsNullOrEmpty(record.RecipeSnapshotJson), "P1: RecipeSnapshotJson 已固化", $"长度={record.RecipeSnapshotJson?.Length ?? 0}");
            Check(record.RecipeVersionNumber == 1, "P1: RecipeVersionNumber=1", $"实际={record.RecipeVersionNumber}");
            Check(record.LeakageLimit == recipeLimit, "P1: 判定限值取配方限值(而非节点限值)", $"实际={record.LeakageLimit}, 期望={recipeLimit}, 节点={nodeLimit}");
            Check(record.FinalLeakageRate > 0, "P1: 最终泄漏率已计算", $"值={record.FinalLeakageRate}");
            Check(record.Result == TestResult.Fail, "P1: 判定结果=Fail(流量~25 > 限值10)", $"实际={record.Result}");

            var process = await ctx.TestProcessData.FirstOrDefaultAsync(p => p.RecordCode == record.RecordCode);
            Check(process != null && !string.IsNullOrEmpty(process.ChannelsJson), "P1: 过程数据 ChannelsJson 已保存");
            Check(process != null && !string.IsNullOrEmpty(process.TimeAxisJson), "P1: 过程数据 TimeAxisJson 已保存");
        }

        // ---- P4 复核：完整监视周期后编辑仍在 ----
        Check(Math.Abs(vm.MonitorVariables[0].MaxDisplay - 123.45) < 0.001, "P4: 监视周期结束后编辑仍保留");

        vm.Dispose();
        return Failures.Count == 0;
    }

    // 切换续采验证用双装置 json（两台均 Modbus + 仿真降级）
    private const string TestPlcJsonMulti = """
    {
      "PlcRegisters": {
        "SampleIntervalMs": 500,
        "Devices": [
          {
            "DeviceCode": "VERIFY-A",
            "Connection": { "PlcType": "Modbus", "IpAddress": "127.0.0.1", "Port": 502, "Protocol": "tcp", "AllowSimulationFallback": true },
            "SampleIntervalMs": 500,
            "Variables": [
              { "VariableCode": "PLC_PRESSURE_P1", "VariableName": "压力P1", "RegisterAddress": 512, "DataType": "double", "Unit": "MPa", "CurveChannel": "Pressure", "MinDisplay": 0, "MaxDisplay": 10000 },
              { "VariableCode": "PLC_FLOW_M1", "VariableName": "流量M1", "RegisterAddress": 804, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow", "MinDisplay": 0, "MaxDisplay": 20000 },
              { "VariableCode": "PLC_FLOW_M2", "VariableName": "流量M2", "RegisterAddress": 806, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow2", "MinDisplay": 0, "MaxDisplay": 20000 }
            ]
          },
          {
            "DeviceCode": "VERIFY-B",
            "Connection": { "PlcType": "Modbus", "IpAddress": "127.0.0.1", "Port": 503, "Protocol": "tcp", "AllowSimulationFallback": true },
            "SampleIntervalMs": 500,
            "Variables": [
              { "VariableCode": "PLC_PRESSURE_P1", "VariableName": "压力P1", "RegisterAddress": 512, "DataType": "double", "Unit": "MPa", "CurveChannel": "Pressure", "MinDisplay": 0, "MaxDisplay": 10000 },
              { "VariableCode": "PLC_FLOW_M1", "VariableName": "流量M1", "RegisterAddress": 804, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow", "MinDisplay": 0, "MaxDisplay": 20000 },
              { "VariableCode": "PLC_FLOW_M2", "VariableName": "流量M2", "RegisterAddress": 806, "DataType": "uint", "Unit": "Nml/min", "CurveChannel": "Flow2", "MinDisplay": 0, "MaxDisplay": 20000 }
            ]
          }
        ]
      }
    }
    """;

    /// <summary>
    /// 监视中切换装置自动续采验证：
    /// 装置A监视中 → 下拉切到B（无弹窗）→ 自动 停止(落盘A记录)→连接B→开新记录 继续采集 → 停止 →
    /// 断言产生两条独立记录（A/B 各自归属、各自过程数据完整）。
    /// </summary>
    private static async Task<bool> RunSwitchVerificationAsync()
    {
        Console.WriteLine("== 准备数据库（重建 IsolationLeakageVerifyDb）==");
        DbContextFactory.Configure(ConnString);
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.MigrateAsync();
        }
        AppServices.Initialize(DbContextFactory.CreateDbContext());

        const string projCode = "VP01", unitCode = "VU01", nodeCode = "V-SW-01";
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.Projects.Add(new Project { Code = projCode, Name = "切换验证项目", Status = EnabledStatus.Enabled });
            ctx.Units.Add(new Unit { Code = unitCode, Name = "切换验证机组", ProjectCode = projCode, Status = EnabledStatus.Enabled });
            ctx.MeasurementDevices.Add(new MeasurementDevice { DeviceCode = "VERIFY-A", DeviceName = "切换装置A", Ip = "127.0.0.1", EnabledStatus = EnabledStatus.Enabled });
            ctx.MeasurementDevices.Add(new MeasurementDevice { DeviceCode = "VERIFY-B", DeviceName = "切换装置B", Ip = "127.0.0.1", EnabledStatus = EnabledStatus.Enabled });
            ctx.TestObjectPathNodes.Add(new TestObjectPathNode
            {
                Code = nodeCode, Name = "切换验证阀门", NodeType = PathNodeType.Valve,
                UnitCode = unitCode, LeakageLimit = 10m, Status = EnabledStatus.Enabled,
            });
            await ctx.SaveChangesAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plc-registers.json"), TestPlcJsonMulti);

        Console.WriteLine("== 创建 ViewModel（多装置模式）==");
        var vm = new RealtimeMonitorViewModel();

        Assert.True(await WaitForAsync(() => vm.AvailableProjects.Any(p => p.Code == projCode), 15000), "项目列表加载");
        vm.SelectedProject = vm.AvailableProjects.First(p => p.Code == projCode);
        Assert.True(await WaitForAsync(() => vm.AvailableUnits.Any(u => u.Code == unitCode), 10000), "机组列表加载");
        vm.SelectedUnit = vm.AvailableUnits.First(u => u.Code == unitCode);
        Assert.True(await WaitForAsync(() => vm.AvailableObjects.Any(o => o.Code == nodeCode), 10000), "试验对象列表加载");
        vm.SelectedObject = vm.AvailableObjects.First(o => o.Code == nodeCode);

        Assert.True(vm.PlcDevices.Count == 2, "多装置模式（2台装置）", $"实际={vm.PlcDevices.Count}");
        Assert.True(vm.SelectedPlcDevice?.DeviceCode == "VERIFY-A", "默认选中第一台 VERIFY-A");

        // 连接 A → 开始监视 → 采集 ~3 秒
        await ((IAsyncRelayCommand)vm.ConnectPlcCommand).ExecuteAsync(null);
        Assert.True(await WaitForAsync(() => vm.IsConnected, 20000), $"连接装置A(仿真降级) — {vm.ConnectionState}");
        await ((IAsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);
        Assert.True(vm.IsMonitoring, "装置A开始监视");
        await Task.Delay(3000);

        // 监视中直接切到 B：不应弹窗，应自动续采（clicker 仅作兜底，若回归出弹窗不至于卡死测试）
        var clicker = Task.Run(async () =>
        {
            for (int i = 0; i < 600; i++) { TryClickOkDialog("提示"); await Task.Delay(50); }
        });
        vm.SelectedPlcDevice = vm.PlcDevices.First(p => p.DeviceCode == "VERIFY-B");

        Assert.True(await WaitForAsync(() => vm.IsMonitoring && vm.SelectedPlcDevice?.DeviceCode == "VERIFY-B", 30000),
            $"切换续采完成（B 监视中）— 状态: {vm.ConnectionState}, 会话: {vm.SessionInfo}");
        await Task.Delay(3000);   // B 采集 ~3 秒

        await ((IAsyncRelayCommand)vm.StopMonitoringCommand).ExecuteAsync(null);
        Assert.True(!vm.IsMonitoring, "停止监视");

        using (var ctx = DbContextFactory.CreateDbContext())
        {
            var records = await ctx.TestRecords.Where(r => r.ObjectCode == nodeCode).OrderBy(r => r.CreatedAt).ToListAsync();
            Check(records.Count == 2, "切换产生两条独立记录", $"实际={records.Count}");
            Check(records.Count > 0 && records[0].DeviceCode == "VERIFY-A", "第一条记录归属装置A", $"实际={records.ElementAtOrDefault(0)?.DeviceCode}");
            Check(records.Count > 1 && records[1].DeviceCode == "VERIFY-B", "第二条记录归属装置B", $"实际={records.ElementAtOrDefault(1)?.DeviceCode}");

            var pA = records.Count > 0 ? await ctx.TestProcessData.FirstOrDefaultAsync(p => p.RecordCode == records[0].RecordCode) : null;
            var pB = records.Count > 1 ? await ctx.TestProcessData.FirstOrDefaultAsync(p => p.RecordCode == records[1].RecordCode) : null;
            Check(pA != null && !string.IsNullOrEmpty(pA.ChannelsJson), "记录A过程数据已落盘");
            Check(pB != null && !string.IsNullOrEmpty(pB.ChannelsJson), "记录B过程数据已落盘");
        }

        vm.Dispose();
        return Failures.Count == 0;
    }

    /// <summary>
    /// CSV 格式变更验证（2026-08 客户三条调整）：
    ///   1. 配方组新表头"预充压压力"（兼容旧"预充压压力P2"）
    ///   2. 实验报表：预充压改名 + 删除试验压力P1/P2 列
    ///   3. 曲线CSV新增"P1的阀开度"列（注册为正式通道，含单位）
    /// </summary>
    private static async Task<bool> RunCsvChecksAsync()
    {
        Console.WriteLine("== 准备数据库（重建 IsolationLeakageVerifyDb）==");
        DbContextFactory.Configure(ConnString);
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.MigrateAsync();
        }
        AppServices.Initialize(DbContextFactory.CreateDbContext());

        // ---- 1a. 配方组：新表头"预充压压力" ----
        var csvNewHeader = "配方名称,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力\r\n" +
                           "配方A,RCV,100,V-CSV-01,50,5,600\r\n";
        var r1 = await AppServices.RecipeService.ImportFromCsvAsync(csvNewHeader, "verify");
        Check(r1.Errors.Count == 0, "配方组: 新表头导入无错误", string.Join(";", r1.Errors));

        // ---- 1b. 配方组：旧表头"预充压压力P2"仍兼容 ----
        var csvOldHeader = "配方名称,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\r\n" +
                           "配方B,RCV,100,V-CSV-02,50,5,700\r\n";
        var r2 = await AppServices.RecipeService.ImportFromCsvAsync(csvOldHeader, "verify");
        Check(r2.Errors.Count == 0, "配方组: 旧表头导入无错误", string.Join(";", r2.Errors));

        using (var ctx = DbContextFactory.CreateDbContext())
        {
            var a = await ctx.TestRecipes.FirstOrDefaultAsync(x => x.RecipeName == "配方A");
            var b = await ctx.TestRecipes.FirstOrDefaultAsync(x => x.RecipeName == "配方B");
            Check(a != null && a.PrechargePressureP2 == 600m, "配方组: 新表头预充压值=600", $"实际={a?.PrechargePressureP2}");
            Check(b != null && b.PrechargePressureP2 == 700m, "配方组: 旧表头预充压值=700", $"实际={b?.PrechargePressureP2}");
        }

        // ---- 2. 实验报表：新格式（预充压压力改名、无试验压力P1/P2 列）----
        var svc = new DataUploadService(AppServices.TestRecordService);
        var report = "序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力,试验仪器读数,实验日期,实验结果\r\n" +
                     "1,RCV,100,V-CSV-01,50,5,600,3.2,2026/8/18 10:00:00,合格\r\n";
        var packages = svc.ParseMultiRowRecordsCsv(report);
        Check(packages.Count == 1, "实验报表: 新格式解析出1条记录", $"实际={packages.Count}");
        var p = packages.FirstOrDefault();
        Check(p != null && p.ObjectCode == "V-CSV-01", "实验报表: 阀门编号", $"实际={p?.ObjectCode}");
        Check(p != null && p.LeakageRate == 3.2m, "实验报表: 试验仪器读数→泄漏率", $"实际={p?.LeakageRate}");
        Check(p != null && p.Result == "合格", "实验报表: 实验结果", $"实际={p?.Result}");
        Check(p != null && p.TestTime != default, "实验报表: 实验日期→试验时间", $"实际={p?.TestTime:yyyy-MM-dd HH:mm:ss}");

        // 嗅探：新格式仍被识别为实验报表（MultiRowRecords）
        var sniff = typeof(DataUploadService)
            .GetMethod("SniffCsvKindFromContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { report })!.ToString();
        Check(sniff == "MultiRowRecords", "实验报表: 新格式嗅探仍为 MultiRowRecords", $"实际={sniff}");

        // ---- 3. 曲线CSV：新增"P1的阀开度"列 → 注册为正式通道 ValveOpeningP1 ----
        var curve = "导出时间,实时压力P1,瞬时流量M1,P1的阀开度\r\n" +
                    "10:00:00,1.5,25,80\r\n" +
                    "10:00:01,1.51,26,81\r\n";
        var pkg = svc.ParseDeviceCsv(curve);
        var points = pkg.ProcessDataPoints;
        Check(points != null && points.Count == 2, "曲线CSV: 解析出2个采样点", $"实际={points?.Count}");
        Check(points != null && points.All(x => x.Channels.ContainsKey("ValveOpeningP1")),
            "曲线CSV: P1的阀开度→通道 ValveOpeningP1");
        Check(points != null && Math.Abs(points[1].Channels["ValveOpeningP1"] - 81) < 0.001,
            "曲线CSV: 阀开度数值正确", $"实际={points?[1].Channels["ValveOpeningP1"]}");

        return Failures.Count == 0;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    // ============ MessageBox 自动点 OK（P3 拦截弹窗用） ============
    private const uint WM_COMMAND = 0x0111;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void TryClickOkDialog(string title)
    {
        var hwnd = FindWindow("#32770", title);
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WM_COMMAND, (IntPtr)1, IntPtr.Zero);   // IDOK
        }
    }

    // 简单断言（失败记录但不中断，便于一次跑完全部场景）
    private static class Assert
    {
        public static void True(bool cond, string name, string detail = "")
        {
            Console.WriteLine($"{(cond ? "[PASS]" : "[FAIL]")} 前置: {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!cond) Failures.Add(name);
        }
    }
}
