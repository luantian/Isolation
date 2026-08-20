using System.Reflection;
using FluentAssertions;
using IsolationLeakage.App.Services;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;
using Role = IsolationLeakage.App.Services.DatabaseFailoverService.DatabaseRole;
using Status = IsolationLeakage.App.Services.DatabaseFailoverService.DatabaseStatus;

namespace IsolationLeakage.App.Tests.Integration;

/// <summary>
/// 数据库高可用（主从故障切换）集成测试。
/// 用真实 SQL 连接驱动状态机：好库=本机 .\SQLEXPRESS/master，坏库=不可达端口（快速失败）。
/// 直接驱动私有 PerformHealthCheck 做确定性断言，另有一条定时器端到端用例验证无死锁。
/// </summary>
[Collection("IntegrationTests")]
public class DatabaseFailoverTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DatabaseFailoverService _svc = DatabaseFailoverService.Instance;

    // 本机可用实例（Windows 集成验证连 master，实例活着即通过）
    private const string GoodConn =
        @"Server=.\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;";
    // 不可达端口 → 连接被拒/超时，快速失败
    private const string BadConn =
        @"Server=localhost,14399;Database=whatever;Integrated Security=true;TrustServerCertificate=true;";

    public DatabaseFailoverTests(ITestOutputHelper output)
    {
        _output = output;
        // 测试进程无 Application.Current：切换/告警路径会同步弹 MessageBox 阻塞测试线程
        AlertService.SuppressUiAlerts = true;
    }

    public void Dispose()
    {
        // 停掉可能启动的定时器，避免污染其它测试
        try { _svc.Stop(); } catch { /* ignore */ }
        AlertService.SuppressUiAlerts = false;
    }

    // ── 反射辅助：单例私有字段/方法 ──
    private static readonly Type T = typeof(DatabaseFailoverService);
    private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic;

    private void SetField(string name, object? value) =>
        T.GetField(name, BF)!.SetValue(_svc, value);

    private TVal GetField<TVal>(string name) =>
        (TVal)T.GetField(name, BF)!.GetValue(_svc)!;

    private void InvokeHealthCheck() =>
        T.GetMethod("PerformHealthCheck", BF)!.Invoke(_svc, null);

    /// <summary>把单例重置到"启用+运行中"的干净起点，并注入主/从连接串。</summary>
    private void Arm(string primary, string secondary, Role role = Role.Primary)
    {
        SetField("_enabled", true);
        SetField("_isRunning", true);
        SetField("_primaryConnectionString", primary);
        SetField("_secondaryConnectionString", secondary);
        SetField("_currentRole", role);
        SetField("_primaryFailureCount", 0);
        SetField("_secondaryFailureCount", 0);
        SetField("_failbackSuccessCount", 0);
        SetField("_connectionTimeoutSeconds", 2);   // 坏库快速失败
        SetField("_maxRetryBeforeFailover", 2);
        SetField("_failbackDelaySeconds", 0);       // 便于测切回
        SetField("_lastFailoverTime", DateTime.MinValue);
    }

    // ── 前置健全性：好库连得上、坏库连不上（否则后面用例无意义）──
    [Fact]
    public void Probe_GoodConnects_BadFails()
    {
        SetField("_connectionTimeoutSeconds", 2);
        var probe = T.GetMethod("TestConnection", BF)!;
        var good = (bool)probe.Invoke(_svc, new object?[] { GoodConn })!;
        var bad = (bool)probe.Invoke(_svc, new object?[] { BadConn })!;
        _output.WriteLine($"good={good}, bad={bad}");
        good.Should().BeTrue("本机 .\\SQLEXPRESS 应可连（前置条件）");
        bad.Should().BeFalse("不可达端口应连接失败");
    }

    // ── 场景1：主库挂 → 连续失败达阈值 → 切到从库 ──
    [Fact]
    public void Primary_Down_FailsOverToSecondary_AfterThreshold()
    {
        Arm(primary: BadConn, secondary: GoodConn, role: Role.Primary);

        InvokeHealthCheck(); // 第1次失败：未达阈值，仍在主库
        _svc.CurrentRole.Should().Be(Role.Primary);
        GetField<int>("_primaryFailureCount").Should().Be(1);

        InvokeHealthCheck(); // 第2次失败：达阈值(2)，切从库
        _svc.CurrentRole.Should().Be(Role.Secondary);
        _svc.CurrentStatus.Should().Be(Status.OnSecondary);
        GetField<int>("_primaryFailureCount").Should().Be(0, "切换后计数应清零");
    }

    // ── 场景2：主库健康 → 始终不切换 ──
    [Fact]
    public void Primary_Healthy_StaysOnPrimary()
    {
        Arm(primary: GoodConn, secondary: GoodConn, role: Role.Primary);

        InvokeHealthCheck();
        InvokeHealthCheck();

        _svc.CurrentRole.Should().Be(Role.Primary);
        _svc.CurrentStatus.Should().Be(Status.Normal);
        GetField<int>("_primaryFailureCount").Should().Be(0);
    }

    // ── 场景3：在从库运行，主库恢复且稳定 → 达阈值后切回主库 ──
    // 注意：从库必须指向带 TestRecords 表的库——回切前的增量安全检查会查询该表
    //（master 无此表会被判定"增量未知"而保守暂停回切，见 P5 修复）。
    [Fact]
    public void OnSecondary_PrimaryRecovers_FailsBackToPrimary()
    {
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
                new SqlCommand("CREATE TABLE TestRecords (TestTime datetime2 NOT NULL)", db).ExecuteNonQuery();
            }

            // 从库正常运行（空表=无增量），主库已恢复；failbackDelay=0 便于立即切回
            Arm(primary: GoodConn, secondary: secondaryConn, role: Role.Secondary);

            InvokeHealthCheck(); // 第1次主库成功：确认中(1/2)，仍在从库
            _svc.CurrentRole.Should().Be(Role.Secondary);
            _svc.CurrentStatus.Should().Be(Status.WaitingFailback);
            GetField<int>("_failbackSuccessCount").Should().Be(1);

            InvokeHealthCheck(); // 第2次成功：达阈值 + 延时已过 + 无增量 → 切回主库
            _svc.CurrentRole.Should().Be(Role.Primary);
            _svc.CurrentStatus.Should().Be(Status.Normal);
        }
        finally
        {
            using var master = new SqlConnection(GoodConn);
            master.Open();
            new SqlCommand(
                $"IF DB_ID('{dbName}') IS NOT NULL ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                master).ExecuteNonQuery();
            new SqlCommand($"DROP DATABASE IF EXISTS [{dbName}]", master).ExecuteNonQuery();
        }
    }

    // ── 场景4：从库也挂了，但主库已恢复 → 立即切回主库 ──
    [Fact]
    public void OnSecondary_SecondaryDown_ButPrimaryUp_ImmediateFailback()
    {
        Arm(primary: GoodConn, secondary: BadConn, role: Role.Secondary);

        InvokeHealthCheck(); // 从库失败但主库恢复 → 立即切回
        _svc.CurrentRole.Should().Be(Role.Primary);
        _svc.CurrentStatus.Should().Be(Status.Normal);
    }

    // ── 场景5：主从全挂 → 切换失败，安全停留在主库并给出提示 ──
    [Fact]
    public void BothDown_FailoverAborts_StaysOnPrimary()
    {
        Arm(primary: BadConn, secondary: BadConn, role: Role.Primary);

        InvokeHealthCheck();
        InvokeHealthCheck(); // 达阈值触发切换，但从库也连不上 → 中止

        _svc.CurrentRole.Should().Be(Role.Primary, "从库不可用时不应切过去");
        _svc.StatusMessage.Should().Contain("均无法连接");
    }

    // ── 场景6：定时器端到端 + 无死锁：主库挂，定时器自动切从库；切换中从别线程调 Stop 不卡死 ──
    [Fact]
    public async Task Timer_AutoFailover_AndStopFromAnotherThread_NoDeadlock()
    {
        Arm(primary: BadConn, secondary: GoodConn, role: Role.Primary);
        SetField("_isRunning", false);            // 让 Start() 能真正启动
        SetField("_healthCheckIntervalSeconds", 1); // 1 秒一检

        _svc.Start();

        // 最多等 8 秒观察切到从库（2 次失败达阈值）
        var switched = await WaitUntil(() => _svc.CurrentRole == Role.Secondary, TimeSpan.FromSeconds(8));
        switched.Should().BeTrue("定时器应在数个周期内自动切到从库");

        // 从另一线程调 Stop：若健康检查仍在锁内做阻塞 I/O 会卡死，这里限时 3 秒
        var stopTask = Task.Run(() => _svc.Stop());
        var stopped = await Task.WhenAny(stopTask, Task.Delay(3000)) == stopTask;
        stopped.Should().BeTrue("Stop() 不应被健康检查阻塞（验证阻塞 I/O 已移出锁）");
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }
}
