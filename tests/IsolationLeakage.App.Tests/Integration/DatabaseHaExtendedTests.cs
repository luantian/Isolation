using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Services;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;
using Role = IsolationLeakage.App.Services.DatabaseFailoverService.DatabaseRole;
using Status = IsolationLeakage.App.Services.DatabaseFailoverService.DatabaseStatus;

namespace IsolationLeakage.App.Tests.Integration;

/// <summary>
/// 数据库高可用扩展测试（补充 DatabaseFailoverTests 六场景之外的缺口）：
/// - 从库角色下双库皆挂（告警路径，不切角色）
/// - 主库闪断一次后恢复 → 触发 DbContext 重建事件（不切换）
/// - 回切延时窗口 → WaitingFailback 状态
/// - 从库增量数据暂停自动回切 / 同步后恢复回切（真实 TestRecords 表）
/// - DbContextFactory 活跃连接串随角色热切换与未启用回落
/// - DataBufferService：成功回放、失败保序回队、毒丸 10 次丢弃、SaveOrBufferAsync 缓冲
/// </summary>
[Collection("IntegrationTests")]
public class DatabaseHaExtendedTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DatabaseFailoverService _svc = DatabaseFailoverService.Instance;

    private const string GoodConn =
        @"Server=.\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;";
    private const string BadConn =
        @"Server=localhost,14399;Database=whatever;Integrated Security=true;TrustServerCertificate=true;";

    public DatabaseHaExtendedTests(ITestOutputHelper output)
    {
        _output = output;
        // 测试进程无 Application.Current：告警会同步弹 MessageBox 阻塞测试线程，必须抑制
        AlertService.SuppressUiAlerts = true;
    }

    public void Dispose()
    {
        try { _svc.Stop(); } catch { /* ignore */ }
        try { DataBufferService.Instance.Clear(); } catch { /* ignore */ }
        // 恢复探测默认行为（真实探测），避免影响其他测试
        DataBufferService.DatabaseReachableProbeOverride = null;
        AlertService.SuppressUiAlerts = false;
    }

    // ── 反射辅助（与 DatabaseFailoverTests 相同手法）──
    private static readonly Type T = typeof(DatabaseFailoverService);
    private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic;

    private void SetField(string name, object? value) =>
        T.GetField(name, BF)!.SetValue(_svc, value);

    private TVal GetField<TVal>(string name) =>
        (TVal)T.GetField(name, BF)!.GetValue(_svc)!;

    private void InvokeHealthCheck() =>
        T.GetMethod("PerformHealthCheck", BF)!.Invoke(_svc, null);

    private void Arm(string primary, string secondary, Role role = Role.Primary,
        int failbackDelaySeconds = 0, DateTime? lastFailoverTime = null)
    {
        SetField("_enabled", true);
        SetField("_isRunning", true);
        SetField("_primaryConnectionString", primary);
        SetField("_secondaryConnectionString", secondary);
        SetField("_currentRole", role);
        SetField("_primaryFailureCount", 0);
        SetField("_secondaryFailureCount", 0);
        SetField("_failbackSuccessCount", 0);
        SetField("_secondaryDataDivergenceAlerted", false);
        SetField("_connectionTimeoutSeconds", 2);
        SetField("_maxRetryBeforeFailover", 2);
        SetField("_failbackDelaySeconds", failbackDelaySeconds);
        SetField("_lastFailoverTime", lastFailoverTime ?? DateTime.MinValue);
    }

    // ── 场景A：从库角色下主从全挂 → 留在从库 + 双挂提示（现有测试只覆盖了主库角色下的中止）──
    [Fact]
    public void OnSecondary_BothDown_StaysOnSecondary_AndAlerts()
    {
        Arm(primary: BadConn, secondary: BadConn, role: Role.Secondary);

        InvokeHealthCheck();

        _svc.CurrentRole.Should().Be(Role.Secondary, "从库角色下双挂不切换角色");
        _svc.StatusMessage.Should().Contain("均无法连接");
        _svc.CurrentStatus.Should().Be(Status.Checking);
    }

    // ── 场景B：主库闪断 1 次后恢复 → 不切换，但触发 DbContext 重建事件 ──
    [Fact]
    public void Primary_BlipThenRecovers_RaisesDbConnectionRebuild_WithoutFailover()
    {
        Arm(primary: BadConn, secondary: GoodConn);

        int events = 0;
        _svc.DbConnectionChanged += () => events++;

        InvokeHealthCheck(); // 失败 1 次（未达阈值 2）
        _svc.CurrentRole.Should().Be(Role.Primary);
        events.Should().Be(0, "尚未恢复，不应触发重建");

        SetField("_primaryConnectionString", GoodConn); // 主库恢复
        InvokeHealthCheck();
        events.Should().Be(1, "主库从失败恢复应触发 DbContext 重建");
        _svc.CurrentRole.Should().Be(Role.Primary, "闪断一次不应切换");
        _svc.CurrentStatus.Should().Be(Status.Normal);

        _svc.DbConnectionChanged -= () => events++; // 无法退订 lambda，测试进程为单例进程级生命周期，可接受
    }

    // ── 场景C：回切延时未到 → WaitingFailback，不切回 ──
    [Fact]
    public void OnSecondary_PrimaryUp_ButWithinFailbackDelay_Waits()
    {
        Arm(primary: GoodConn, secondary: GoodConn, role: Role.Secondary,
            failbackDelaySeconds: 600, lastFailoverTime: DateTime.Now.AddSeconds(-10));

        InvokeHealthCheck();
        InvokeHealthCheck(); // 连续 2 次成功达到阈值，但延时未到

        _svc.CurrentRole.Should().Be(Role.Secondary, "回切延时窗口内不应切回");
        _svc.CurrentStatus.Should().Be(Status.WaitingFailback);
        _svc.StatusMessage.Should().Contain("等待");
    }

    // ── 场景D：从库有增量数据 → 暂停自动回切；同步后 → 恢复回切（真实 TestRecords 表）──
    [Fact]
    public async Task OnSecondary_IncrementalData_BlocksFailback_UntilSynced()
    {
        // 在本机实例上建临时库 + TestRecords 表 + 一条"切换后新增"的记录
        var dbName = $"FailoverHaTest_{Guid.NewGuid():N}";
        string secondaryConn =
            $@"Server=.\SQLEXPRESS;Database={dbName};Integrated Security=true;TrustServerCertificate=true;";
        using (var master = new SqlConnection(GoodConn))
        {
            master.Open();
            new SqlCommand($"CREATE DATABASE [{dbName}]", master).ExecuteNonQuery();
        }
        try
        {
            using (var db = new SqlConnection(secondaryConn))
            {
                db.Open();
                new SqlCommand(
                    "CREATE TABLE TestRecords (TestTime datetime2 NOT NULL)", db).ExecuteNonQuery();
                new SqlCommand("INSERT INTO TestRecords (TestTime) VALUES (@t)", db)
                {
                    Parameters = { new SqlParameter("@t", DateTime.Now) }
                }.ExecuteNonQuery();
            }

            Arm(primary: GoodConn, secondary: secondaryConn, role: Role.Secondary,
                failbackDelaySeconds: 0, lastFailoverTime: DateTime.Now.AddMinutes(-5));

            InvokeHealthCheck();
            InvokeHealthCheck(); // 2 次成功达阈值 + 延时已过 → 尝试回切 → 检测到增量 → 暂停

            _svc.CurrentRole.Should().Be(Role.Secondary, "从库有增量数据时不应自动回切");
            _svc.StatusMessage.Should().Contain("已暂停自动回切");
            GetField<bool>("_secondaryDataDivergenceAlerted").Should().BeTrue("应记录已告警避免重复弹窗");

            // 人工同步：清掉增量
            using (var db = new SqlConnection(secondaryConn))
            {
                db.Open();
                new SqlCommand("DELETE FROM TestRecords", db).ExecuteNonQuery();
            }

            InvokeHealthCheck();
            InvokeHealthCheck(); // 增量为 0 → 自动回切

            _svc.CurrentRole.Should().Be(Role.Primary, "增量清零后应恢复自动回切");
            _svc.CurrentStatus.Should().Be(Status.Normal);
        }
        finally
        {
            using var master = new SqlConnection(GoodConn);
            master.Open();
            KillAllConnections(master, dbName);
            new SqlCommand($"DROP DATABASE IF EXISTS [{dbName}]", master).ExecuteNonQuery();
        }
    }

    private static void KillAllConnections(SqlConnection master, string dbName)
    {
        try
        {
            new SqlCommand(
                $"IF DB_ID('{dbName}') IS NOT NULL ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                master).ExecuteNonQuery();
        }
        catch { /* best effort */ }
    }

    // ── 场景D2（P5 修复）：从库缺 TestRecords 表 → 增量检查失败 → 保守暂停回切（不得放行）──
    [Fact]
    public void OnSecondary_IncrementCheckFails_BlocksFailback_Conservatively()
    {
        // 建临时库但不建 TestRecords 表（模拟从库结构不完整/备份残缺）
        var dbName = $"FailoverHaTest_{Guid.NewGuid():N}";
        string secondaryConn =
            $@"Server=.\SQLEXPRESS;Database={dbName};Integrated Security=true;TrustServerCertificate=true;";
        using (var master = new SqlConnection(GoodConn))
        {
            master.Open();
            new SqlCommand($"CREATE DATABASE [{dbName}]", master).ExecuteNonQuery();
        }
        try
        {
            Arm(primary: GoodConn, secondary: secondaryConn, role: Role.Secondary,
                failbackDelaySeconds: 0, lastFailoverTime: DateTime.Now.AddMinutes(-5));

            InvokeHealthCheck();
            InvokeHealthCheck(); // 主库连续 2 次成功达回切条件 → 增量查询抛错（无表）→ 应暂停而非放行

            _svc.CurrentRole.Should().Be(Role.Secondary, "增量检查失败时必须暂停自动回切（防静默数据分叉）");
            _svc.StatusMessage.Should().Contain("已暂停自动回切").And.Contain("检查失败");
        }
        finally
        {
            using var master = new SqlConnection(GoodConn);
            master.Open();
            KillAllConnections(master, dbName);
            new SqlCommand($"DROP DATABASE IF EXISTS [{dbName}]", master).ExecuteNonQuery();
        }
    }

    // ── 场景E2（P4 修复）：手动切换/配置重载后复位分叉告警标志 ──
    [Fact]
    public void ForceSwitchTo_ResetsDivergenceAlertFlag()
    {
        Arm(primary: GoodConn, secondary: GoodConn);
        SetField("_secondaryDataDivergenceAlerted", true);

        _svc.ForceSwitchTo(Role.Primary);
        GetField<bool>("_secondaryDataDivergenceAlerted").Should().BeFalse("手动强制切换应复位分叉告警标志");

        SetField("_secondaryDataDivergenceAlerted", true);
        _svc.ReloadConfiguration();
        GetField<bool>("_secondaryDataDivergenceAlerted").Should().BeFalse("配置重载应复位分叉告警标志");
    }

    // ── 场景E：DbContextFactory 活跃连接串随角色热切换；未启用时回落 ──
    [Fact]
    public void DbContextFactory_FollowsActiveRole_AndFallsBackWhenDisabled()
    {
        Arm(primary: BadConn, secondary: GoodConn);

        // 保存 DbContextFactory 静态手动配置，测试后还原
        var factoryType = typeof(DbContextFactory);
        var csField = factoryType.GetField("_connectionString", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = (string?)csField.GetValue(null);
        try
        {
            csField.SetValue(null, "Server=manual;Database=x;Integrated Security=true;");

            // 启用 failover 时：活跃库连接最优先（手动配置不得短路 failover）
            DbContextFactory.GetActiveConnectionString().Should().Be(BadConn, "主库角色应返回主库连接串");

            _svc.ForceSwitchTo(Role.Secondary);
            DbContextFactory.GetActiveConnectionString().Should().Be(GoodConn, "切到从库后应返回从库连接串");

            _svc.ForceSwitchTo(Role.Primary);
            DbContextFactory.GetActiveConnectionString().Should().Be(BadConn, "切回主库后应返回主库连接串");

            // 未启用 failover 时：回落到手动配置
            SetField("_enabled", false);
            DbContextFactory.GetActiveConnectionString().Should().Be("Server=manual;Database=x;Integrated Security=true;",
                "未启用 failover 时应使用手动配置的连接串");
        }
        finally
        {
            csField.SetValue(null, original);
            SetField("_enabled", true);
        }
    }

    // ── 场景F1：缓冲项回放成功后清空 ──
    [Fact]
    public async Task Buffer_FlushSuccess_DrainsQueue()
    {
        var buffer = DataBufferService.Instance;
        buffer.Clear();
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(true);

        for (int i = 0; i < 3; i++)
            buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, $"item{i}", 100, () => Task.FromResult(true));

        buffer.BufferCount.Should().Be(3);
        await buffer.FlushAsync();
        buffer.BufferCount.Should().Be(0, "全部成功后队列应清空");
    }

    // ── 场景F2：失败项停止本轮并按原顺序回队（不丢后续项）──
    [Fact]
    public async Task Buffer_FailureStopsFlush_AndRequeuesInOrder()
    {
        var buffer = DataBufferService.Instance;
        buffer.Clear();
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(true);

        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "A", 100, () => Task.FromResult(true));
        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "B", 100, () => Task.FromResult(false));
        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "C", 100, () => Task.FromResult(true));

        await buffer.FlushAsync();

        buffer.BufferCount.Should().Be(2, "A 成功出队，B 失败应连同 C 回队");
        var queue = GetBufferQueue();
        string.Join(",", queue.Select(i => i.Description)).Should().Be("B,C", "回队应保持原顺序");

        // 后续全部成功 → 清空（验证回队项可被再次处理）
        foreach (var item in queue)
            item.RetryAction = () => Task.FromResult(true);
        await buffer.FlushAsync();
        buffer.BufferCount.Should().Be(0);
    }

    // ── 场景F3：毒丸防护——单项连续失败 10 次后丢弃，且元数据落盘待生成恢复报告 ──
    // 语义确认：队头失败会停止整轮并全部回队（保序），因此每轮失败后 poison 与 behind 均在队。
    [Fact]
    public async Task Buffer_PoisonItem_DroppedAfterTenFailures()
    {
        var buffer = DataBufferService.Instance;
        buffer.Clear();
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(true);

        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "poison", 100, () => Task.FromResult(false));
        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "behind", 100, () => Task.FromResult(true));
        var poisonId = GetBufferQueue().Single(i => i.Description == "poison").Id;

        for (int round = 1; round <= 9; round++)
        {
            await buffer.FlushAsync();
            buffer.BufferCount.Should().Be(2, $"第 {round} 轮队头失败应停止整轮：poison 与 behind 均回队（10 次内不丢弃）");
            GetBufferQueue().Single(i => i.Description == "poison").RetryCount.Should().Be(round);
        }

        await buffer.FlushAsync(); // 第 10 次失败 → 丢弃毒丸（continue），behind 同轮成功出队
        buffer.BufferCount.Should().Be(0, "毒丸丢弃后同轮应继续处理 behind");
        GetBufferQueue().Select(i => i.Description).Should().NotContain("poison", "10 次失败后毒丸应被丢弃");

        // 丢弃项元数据应落盘（下次启动生成恢复报告）
        var diskDir = Path.Combine(AppContext.BaseDirectory, "BufferedData");
        File.Exists(Path.Combine(diskDir, $"buffer_{poisonId:N}.json"))
            .Should().BeTrue("毒丸丢弃时应把元数据落盘，供下次启动生成恢复报告");
    }

    // ── 场景F5（P1 修复）：数据库不可达（宕机熔断）期间不累计失败次数、不触发毒丸丢弃 ──
    [Fact]
    public async Task Buffer_DbUnreachable_SkipsFlush_WithoutCountingFailures()
    {
        var buffer = DataBufferService.Instance;
        buffer.Clear();
        // 模拟主库宕机 + 切换窗口：探测不可达
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(false);

        buffer.Buffer(DataBufferService.BufferOperationType.SaveOther, "outage-item", 100, () => Task.FromResult(false));

        // 宕机期间反复触发刷新（模拟 5 秒定时器空转，远超原 50 秒毒丸窗口）
        for (int round = 1; round <= 20; round++)
            await buffer.FlushAsync();

        buffer.BufferCount.Should().Be(1, "宕机期间数据必须保留在队列");
        GetBufferQueue().Single().RetryCount.Should().Be(0, "宕机期间不累计失败次数（否则 50 秒后被毒丸误丢弃）");

        // 数据库恢复后正常回放
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(true);
        foreach (var item in GetBufferQueue())
            item.RetryAction = () => Task.FromResult(true);
        await buffer.FlushAsync();
        buffer.BufferCount.Should().Be(0, "恢复后缓冲数据应正常写入");
    }

    // ── 场景F4：SaveOrBufferAsync 写入异常 → 自动缓冲 ──
    [Fact]
    public async Task SaveOrBuffer_Failure_BuffersItem()
    {
        var buffer = DataBufferService.Instance;
        buffer.Clear();
        DataBufferService.DatabaseReachableProbeOverride = () => Task.FromResult(true);

        var result = await buffer.SaveOrBufferAsync(
            DataBufferService.BufferOperationType.SaveOther, "fail-once", 100,
            saveAction: () => throw new SqlExceptionMock());

        result.Should().BeFalse("写入失败应返回 false");
        buffer.BufferCount.Should().Be(1, "失败后应进入缓冲");

        // 让重试成功：把队列项的重试动作替换为成功
        foreach (var item in GetBufferQueue())
            item.RetryAction = () => Task.FromResult(true);
        await buffer.FlushAsync();
        buffer.BufferCount.Should().Be(0);
    }

    private static List<DataBufferService.BufferedItem> GetBufferQueue()
    {
        var field = typeof(DataBufferService).GetField("_buffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queue = (ConcurrentQueue<DataBufferService.BufferedItem>)field
            .GetValue(DataBufferService.Instance)!;
        return queue.ToList();
    }

    private sealed class SqlExceptionMock : Exception
    {
        public SqlExceptionMock() : base("模拟数据库写入失败") { }
    }
}
