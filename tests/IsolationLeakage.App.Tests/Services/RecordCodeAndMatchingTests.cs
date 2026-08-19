using System.Reflection;
using FluentAssertions;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// 数据导入记录编号与曲线↔汇总配对回归测试（提交 fb2fe99 修复项）。
/// BuildRecordCode / FindClosestSummaryRecord / NormalizeCsvField 为 DataUploadService
/// 的私有纯函数，经反射调用（不为此改产品代码可见性）。
/// </summary>
public sealed class RecordCodeAndMatchingTests
{
    // 注意：BuildRecordCode 已改为 public（供 RealtimeMonitor 等模块复用），
    // 绑定标志同时含 Public/NonPublic 以兼容可见性调整
    private static readonly MethodInfo BuildRecordCodeMethod = typeof(DataUploadService)
        .GetMethod("BuildRecordCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo FindClosestMethod = typeof(DataUploadService)
        .GetMethod("FindClosestSummaryRecord", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo NormalizeMethod = typeof(DataUploadService)
        .GetMethod("NormalizeCsvField", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly DateTime TestTime = new(2026, 8, 19, 14, 30, 25, 123);

    private static string BuildRecordCode(string? project, string? unit, string? objectCode, string leaf, DateTime time)
        => (string)BuildRecordCodeMethod.Invoke(null, new object?[] { project, unit, objectCode, leaf, time })!;

    // FindClosestSummaryRecord 含第三参 excludeRecordCodes（已挂载曲线的汇总排除集，防相邻试验错挂）；
    // 测试统一传空集合
    private static TestRecord? FindClosest(Dictionary<string, TestRecord> records, string curveRecordCode)
        => (TestRecord?)FindClosestMethod.Invoke(null,
            new object?[] { records, new HashSet<string>(), curveRecordCode });

    private static string? Normalize(string? value)
        => (string?)NormalizeMethod.Invoke(null, new[] { value });

    // ====== BuildRecordCode ======

    [Fact]
    public void BuildRecordCode_ShortCodes_FullFormat()
    {
        var code = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        code.Should().Be($"P2608_U01_V-001_{TestTime:yyyyMMddHHmmssfff}");
        code.Length.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public void BuildRecordCode_EmptyObject_FallsBackToLeafCode()
    {
        // 多行 CSV 场景：对象编号在 recordCode 生成之后才回填，空对象须回退叶子编码
        var code = BuildRecordCode("P2608", "U01", null, "V-002", TestTime);
        code.Should().Be($"P2608_U01_V-002_{TestTime:yyyyMMddHHmmssfff}", "空对象编码应回退路径叶子编码");
    }

    [Fact]
    public void BuildRecordCode_LongValveTag_TruncatedTo50Chars()
    {
        // 核电阀门位号可达数十字符；TestRecord.RecordCode 上限 50，不截断会触发 SQL 截断异常
        var longTag = new string('V', 60);
        var code = BuildRecordCode("P2608", "U01", longTag, "V-001", TestTime);
        code.Length.Should().BeLessThanOrEqualTo(50, "核电长位号必须被预算截断");
        code.Should().EndWith($"_{TestTime:yyyyMMddHHmmssfff}", "截断只作用于对象段，时间戳保证唯一性");
    }

    [Fact]
    public void BuildRecordCode_VeryLongPrefix_StillHasTimestampUniqueness()
    {
        // 项目+机组编码合计极长（prefix+suffix 已超 50）时对象段至少保留 1 字符；
        // 极端情况下整体超长会撞库，但常规编码长度下不会发生
        var code = BuildRecordCode(new string('P', 20), new string('U', 20), "V-001", "V-001", TestTime);
        code.Should().NotBeNullOrEmpty();
    }

    // ====== FindClosestSummaryRecord ======

    private static Dictionary<string, TestRecord> SingleSummaryRecord(DateTime summaryTime, string objectCode = "V-001")
    {
        var summaryCode = BuildRecordCode("P2608", "U01", objectCode, objectCode, summaryTime);
        return new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase)
        {
            [summaryCode] = new TestRecord { RecordCode = summaryCode, ObjectCode = objectCode }
        };
    }

    [Fact]
    public void FindClosest_MillisecondDifference_Matches()
    {
        // 核心场景：汇总用报表"试验时间"、曲线用首行采样时间，毫秒必然不同
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        var records = SingleSummaryRecord(TestTime.AddMilliseconds(-456));

        var match = FindClosest(records, curveCode);

        match.Should().NotBeNull("毫秒级差异必须回退匹配成功，否则同一试验生成两条记录");
    }

    [Fact]
    public void FindClosest_SecondsDifference_Matches()
    {
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        var records = SingleSummaryRecord(TestTime.AddSeconds(-8));

        FindClosest(records, curveCode).Should().NotBeNull("数秒差异（采样起点 vs 试验时刻）在 ±5 分钟窗口内");
    }

    [Fact]
    public void FindClosest_BeyondFiveMinutes_ReturnsNull()
    {
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        var records = SingleSummaryRecord(TestTime.AddMinutes(-10));

        FindClosest(records, curveCode).Should().BeNull("超窗不匹配，宁可单独导入也不挂错记录");
    }

    [Fact]
    public void FindClosest_DifferentObjectPrefix_ReturnsNull()
    {
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        var records = SingleSummaryRecord(TestTime, objectCode: "V-999");

        FindClosest(records, curveCode).Should().BeNull("对象前缀不同（其它阀门的记录）不得匹配");
    }

    [Fact]
    public void FindClosest_MultipleCandidates_PicksClosest()
    {
        // 同对象 5 分钟内多条汇总（连续批量试验）：取时间差最小者
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        var nearCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime.AddSeconds(-5));
        var farCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime.AddMinutes(-3));
        var records = new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase)
        {
            [farCode] = new TestRecord { RecordCode = farCode },
            [nearCode] = new TestRecord { RecordCode = nearCode },
        };

        var match = FindClosest(records, curveCode);

        match.Should().NotBeNull();
        match!.RecordCode.Should().Be(nearCode, "应匹配时间差最小的汇总记录");
    }

    [Fact]
    public void FindClosest_EmptyDictionary_ReturnsNull()
    {
        var curveCode = BuildRecordCode("P2608", "U01", "V-001", "V-001", TestTime);
        FindClosest([], curveCode).Should().BeNull();
    }

    // ====== NormalizeCsvField ======

    [Theory]
    [InlineData("空")]
    [InlineData("NULL")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("/")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_PlaceholderValues_ReturnsNull(string? value)
    {
        // 客户报表用"空"等作空单元格占位——按空值处理，否则真实创建名为"空"的阀门/系统
        Normalize(value).Should().BeNull($"占位值「{value}」应视为空");
    }

    [Theory]
    [InlineData(" V-001 ", "V-001")]
    [InlineData("V-001", "V-001")]
    [InlineData("系统A", "系统A")]
    public void Normalize_RealValues_Trimmed(string value, string expected)
    {
        Normalize(value).Should().Be(expected);
    }
}
