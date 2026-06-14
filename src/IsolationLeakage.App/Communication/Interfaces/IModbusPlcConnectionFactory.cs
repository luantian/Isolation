using IsolationLeakage.App.Communication.Interfaces;

namespace IsolationLeakage.App.Communication.Interfaces;

/// <summary>
/// Modbus PLC 连接工厂接口
/// </summary>
public interface IModbusPlcConnectionFactory
{
    /// <summary>
    /// 创建 Modbus PLC 连接
    /// </summary>
    /// <param name="protocol">传输模式："tcp" 或 "rtu"</param>
    IModbusPlcConnection Create(string protocol = "tcp");
}
