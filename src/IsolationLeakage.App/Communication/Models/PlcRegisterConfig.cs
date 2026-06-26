using System.Text.Json.Serialization;

namespace IsolationLeakage.App.Communication.Models;

/// <summary>
/// PLC 变量配置（对应 plc-registers.json 中的单个变量定义）
/// </summary>
public class PlcVariableConfig
{
    /// <summary>
    /// 变量编码（唯一标识，如 PLC_PRESSURE_MAIN）
    /// </summary>
    [JsonPropertyName("VariableCode")]
    public string VariableCode { get; set; } = string.Empty;

    /// <summary>
    /// 变量中文名（如"主压力"）
    /// </summary>
    [JsonPropertyName("VariableName")]
    public string VariableName { get; set; } = string.Empty;

    /// <summary>
    /// 寄存器起始地址
    /// </summary>
    [JsonPropertyName("RegisterAddress")]
    public int RegisterAddress { get; set; }

    /// <summary>
    /// 数据类型：double（2个寄存器）或 ushort（1个寄存器）
    /// </summary>
    [JsonPropertyName("DataType")]
    public string DataType { get; set; } = "double";

    /// <summary>
    /// 单位（如 MPa、L/min、℃）
    /// </summary>
    [JsonPropertyName("Unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// 关联的曲线通道：Pressure、Flow、Temp，null 表示不显示曲线
    /// </summary>
    [JsonPropertyName("CurveChannel")]
    public string? CurveChannel { get; set; }

    /// <summary>
    /// 曲线显示最小值
    /// </summary>
    [JsonPropertyName("MinDisplay")]
    public double MinDisplay { get; set; }

    /// <summary>
    /// 曲线显示最大值
    /// </summary>
    [JsonPropertyName("MaxDisplay")]
    public double MaxDisplay { get; set; }
}

/// <summary>
/// PLC 连接配置
/// </summary>
public class PlcConnectionConfig
{
    /// <summary>
    /// PLC IP 地址
    /// </summary>
    [JsonPropertyName("IpAddress")]
    public string IpAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Modbus TCP 端口
    /// </summary>
    [JsonPropertyName("Port")]
    public int Port { get; set; } = 502;

    /// <summary>
    /// 协议类型：tcp 或 rtu
    /// </summary>
    [JsonPropertyName("Protocol")]
    public string Protocol { get; set; } = "tcp";
}

/// <summary>
/// PLC 寄存器配置根节点
/// </summary>
public class PlcRegistersSection
{
    /// <summary>
    /// PLC 连接配置
    /// </summary>
    [JsonPropertyName("Connection")]
    public PlcConnectionConfig Connection { get; set; } = new();

    /// <summary>
    /// 变量寄存器列表
    /// </summary>
    [JsonPropertyName("Variables")]
    public List<PlcVariableConfig> Variables { get; set; } = [];

    /// <summary>
    /// 采样周期（毫秒），默认 1000ms
    /// </summary>
    [JsonPropertyName("SampleIntervalMs")]
    public int SampleIntervalMs { get; set; } = 1000;
}
