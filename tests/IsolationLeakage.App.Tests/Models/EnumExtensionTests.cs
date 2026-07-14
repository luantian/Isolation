using FluentAssertions;
using IsolationLeakage.App.Models;
using Xunit;

namespace IsolationLeakage.App.Tests.Models;

/// <summary>
/// 枚举扩展方法测试
/// </summary>
public class EnumExtensionTests
{
    // ── EnabledStatus ──

    [Theory]
    [InlineData(EnabledStatus.Enabled, "启用")]
    [InlineData(EnabledStatus.Disabled, "停用")]
    public void EnabledStatus_ToText(EnabledStatus status, string expected)
    {
        status.ToText().Should().Be(expected);
    }

    // ── TestResult ──

    [Theory]
    [InlineData(TestResult.Pass, "合格")]
    [InlineData(TestResult.Fail, "不合格")]
    [InlineData(TestResult.Unknown, "未知")]
    public void TestResult_ToText(TestResult result, string expected)
    {
        result.ToText().Should().Be(expected);
    }

    // ── PathNodeType ──

    [Theory]
    [InlineData(PathNodeType.System, "系统")]
    [InlineData(PathNodeType.Penetration, "贯穿件")]
    [InlineData(PathNodeType.Valve, "阀门")]
    [InlineData(PathNodeType.OtherComponent, "其他部件")]
    public void PathNodeType_ToText(PathNodeType type, string expected)
    {
        type.ToText().Should().Be(expected);
    }

    // ── CommunicationType ──

    [Theory]
    [InlineData(CommunicationType.Usb, "USB")]
    [InlineData(CommunicationType.Rj45, "RJ45")]
    [InlineData(CommunicationType.Rs232, "RS232")]
    [InlineData(CommunicationType.Rs485, "RS485")]
    [InlineData(CommunicationType.Other, "其他")]
    public void CommunicationType_ToText(CommunicationType type, string expected)
    {
        type.ToText().Should().Be(expected);
    }

    // ── ConnectionStatus ──

    [Theory]
    [InlineData(ConnectionStatus.Online, "在线")]
    [InlineData(ConnectionStatus.Offline, "离线")]
    [InlineData(ConnectionStatus.Unknown, "未知")]
    public void ConnectionStatus_ToText(ConnectionStatus status, string expected)
    {
        status.ToText().Should().Be(expected);
    }
}
