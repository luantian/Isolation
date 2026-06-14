using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Models;

/// <summary>
/// 连接配置基类
/// </summary>
public abstract class ConnectionConfig
{
    /// <summary>通讯方式</summary>
    public CommunicationType TransportType { get; set; }

    /// <summary>超时时间（默认 30 秒）</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>重试次数（默认 3 次）</summary>
    public int RetryCount { get; set; } = 3;
}

/// <summary>
/// USB 大容量存储连接配置
/// </summary>
public sealed class UsbConfig : ConnectionConfig
{
    /// <summary>U 盘盘符（如 E）</summary>
    public string DriveLetter { get; set; } = "E";

    /// <summary>任务文件夹</summary>
    public string TaskFolder { get; set; } = "tasks";

    /// <summary>结果文件夹</summary>
    public string ResultFolder { get; set; } = "results";

    /// <summary>归档文件夹</summary>
    public string ArchiveFolder { get; set; } = "archive";

    public UsbConfig()
    {
        TransportType = CommunicationType.Usb;
    }
}

/// <summary>
/// TCP/IP 网络连接配置
/// </summary>
public sealed class TcpConfig : ConnectionConfig
{
    /// <summary>设备 IP 地址</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>端口号</summary>
    public int Port { get; set; } = 8080;

    /// <summary>API 根路径</summary>
    public string BasePath { get; set; } = "/api/v1";

    public TcpConfig()
    {
        TransportType = CommunicationType.Rj45;
    }
}

/// <summary>
/// 串口连接配置（RS232 / RS485）
/// </summary>
public sealed class SerialConfig : ConnectionConfig
{
    /// <summary>串口名称（如 COM3）</summary>
    public string PortName { get; set; } = "COM3";

    /// <summary>波特率（默认 9600）</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>数据位（默认 8）</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>奇偶校验</summary>
    public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;

    /// <summary>停止位</summary>
    public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;

    public SerialConfig()
    {
        TransportType = CommunicationType.Rs232;
    }
}
