namespace IsolationLeakage.App.Models;

/// <summary>
/// 启用状态
/// </summary>
public enum EnabledStatus
{
    Disabled = 0,
    Enabled = 1,
}

public static class EnabledStatusExtensions
{
    public static string ToText(this EnabledStatus status) => status switch
    {
        EnabledStatus.Enabled => "启用",
        EnabledStatus.Disabled => "停用",
        _ => "未知"
    };
}

/// <summary>
/// 试验结果
/// </summary>
public enum TestResult
{
    Unknown = 0,
    Pass = 1,
    Fail = 2,
}

public static class TestResultExtensions
{
    public static string ToText(this TestResult result) => result switch
    {
        TestResult.Pass => "合格",
        TestResult.Fail => "不合格",
        _ => "未知"
    };
}

/// <summary>
/// 路径节点类型
/// </summary>
public enum PathNodeType
{
    System = 1,
    Penetration = 2,
    Valve = 3,
    OtherComponent = 4,
}

public static class PathNodeTypeExtensions
{
    public static string ToText(this PathNodeType type) => type switch
    {
        PathNodeType.System => "系统",
        PathNodeType.Penetration => "贯穿件",
        PathNodeType.Valve => "阀门",
        PathNodeType.OtherComponent => "其他部件",
        _ => "未知"
    };
}

/// <summary>
/// 通信方式
/// </summary>
public enum CommunicationType
{
    Usb = 1,
    Rj45 = 2,
    Rs232 = 3,
    Rs485 = 4,
    Other = 99,
}

public static class CommunicationTypeExtensions
{
    public static string ToText(this CommunicationType type) => type switch
    {
        CommunicationType.Usb => "USB",
        CommunicationType.Rj45 => "RJ45",
        CommunicationType.Rs232 => "RS232",
        CommunicationType.Rs485 => "RS485",
        _ => "其他"
    };
}

/// <summary>
/// 连接状态
/// </summary>
public enum ConnectionStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
}

public static class ConnectionStatusExtensions
{
    public static string ToText(this ConnectionStatus status) => status switch
    {
        ConnectionStatus.Online => "在线",
        ConnectionStatus.Offline => "离线",
        _ => "未知"
    };
}
