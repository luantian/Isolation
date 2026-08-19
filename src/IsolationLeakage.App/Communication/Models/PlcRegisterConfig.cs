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
    /// 寄存器起始地址（Modbus 协议使用）
    /// </summary>
    [JsonPropertyName("RegisterAddress")]
    public int RegisterAddress { get; set; }

    /// <summary>
    /// 西门子地址格式（S7 协议使用）
    /// 格式示例：DB15.DBD0, DB15.DBW0, DB15.DBB0
    /// </summary>
    [JsonPropertyName("SiemensAddress")]
    public string SiemensAddress { get; set; } = string.Empty;

    /// <summary>
    /// 数据类型：
    /// - Modbus: double（2个寄存器）、ushort（1个寄存器）
    /// - Siemens S7: real、float、double（Real）、word、ushort、int（Word）、dword、uint（DWord）、byte
    /// </summary>
    [JsonPropertyName("DataType")]
    public string DataType { get; set; } = "double";

    /// <summary>
    /// 单位（如 MPa、Nml/min、℃）
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
    /// PLC 类型：Modbus 或 SiemensS7
    /// </summary>
    [JsonPropertyName("PlcType")]
    public string PlcType { get; set; } = "Modbus";

    /// <summary>
    /// PLC IP 地址
    /// </summary>
    [JsonPropertyName("IpAddress")]
    public string IpAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// 端口：Modbus 默认 502，S7 协议默认 102
    /// </summary>
    [JsonPropertyName("Port")]
    public int Port { get; set; } = 502;

    /// <summary>
    /// 协议类型：
    /// - Modbus: tcp 或 rtu
    /// - SiemensS7: S71200, S71500, S7300, S7400, S7200
    /// </summary>
    [JsonPropertyName("Protocol")]
    public string Protocol { get; set; } = "tcp";

    /// <summary>
    /// 西门子 PLC Rack 号（通常为 0）
    /// </summary>
    [JsonPropertyName("Rack")]
    public short Rack { get; set; } = 0;

    /// <summary>
    /// 西门子 PLC Slot 号（S7-1200/1500 通常为 1，S7-300 通常为 2）
    /// </summary>
    [JsonPropertyName("Slot")]
    public short Slot { get; set; } = 1;

    /// <summary>
    /// 连接失败时是否降级为仿真（模拟）数据。
    /// 默认 false：连接失败直接报错并把原因写入 logs 日志，方便排查现场通信问题；
    /// 仅在演示/无 PLC 环境时设为 true 才会显示仿真曲线。
    /// </summary>
    [JsonPropertyName("AllowSimulationFallback")]
    public bool AllowSimulationFallback { get; set; } = false;
}

/// <summary>
/// 按装置的 PLC 配置（多设备模式）。
/// DeviceCode 建议与测量装置台账（MeasurementDevices.DeviceCode）一致：
/// 获得台账 IP 覆盖、勾选关联，且主装置必须台账存在以满足 TestRecord 外键。
/// 不同装置的 VariableCode 允许重复（内部以 "DeviceCode:VariableCode" 区分）。
/// </summary>
public class PlcDeviceConfig
{
    /// <summary>装置编码（对应台账 DeviceCode；单装置旧格式归一化为 "DEFAULT"）</summary>
    [JsonPropertyName("DeviceCode")]
    public string DeviceCode { get; set; } = "DEFAULT";

    /// <summary>该装置的连接配置</summary>
    [JsonPropertyName("Connection")]
    public PlcConnectionConfig Connection { get; set; } = new();

    /// <summary>该装置的变量列表</summary>
    [JsonPropertyName("Variables")]
    public List<PlcVariableConfig> Variables { get; set; } = [];

    /// <summary>该装置的采样周期（毫秒）；0 表示沿用全局 SampleIntervalMs</summary>
    [JsonPropertyName("SampleIntervalMs")]
    public int SampleIntervalMs { get; set; }
}

/// <summary>
/// PLC 寄存器配置根节点
/// </summary>
public class PlcRegistersSection
{
    /// <summary>
    /// PLC 连接配置（单装置旧格式；有 Devices 时忽略）
    /// </summary>
    [JsonPropertyName("Connection")]
    public PlcConnectionConfig Connection { get; set; } = new();

    /// <summary>
    /// 变量寄存器列表（单装置旧格式；有 Devices 时忽略）
    /// </summary>
    [JsonPropertyName("Variables")]
    public List<PlcVariableConfig> Variables { get; set; } = [];

    /// <summary>
    /// 采样周期（毫秒），默认 1000ms（单装置旧格式的全局默认）
    /// </summary>
    [JsonPropertyName("SampleIntervalMs")]
    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 多设备配置列表。为空时由 GetPlcRegisters() 归一化为单装置 DEFAULT
    /// （Connection/Variables/SampleIntervalMs 透传），旧格式文件无需修改。
    /// </summary>
    [JsonPropertyName("Devices")]
    public List<PlcDeviceConfig>? Devices { get; set; }
}
