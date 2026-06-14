using System.IO;
using IsolationLeakage.App.Communication.Implementations;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication;

/// <summary>
/// 设备连接工厂实现
/// </summary>
public sealed class DeviceConnectionFactory : IConnectionFactory
{
    /// <summary>
    /// 创建指定通讯方式的连接实例
    /// </summary>
    public IDeviceConnection Create(CommunicationType type)
    {
        return type switch
        {
            CommunicationType.Usb => new UsbMassStorageConnection(),
            CommunicationType.Rj45 => new TcpIpConnection(),
            CommunicationType.Rs232 => new SerialConnection(),
            CommunicationType.Rs485 => new SerialConnection(), // 同串口实现，仅波特率不同
            CommunicationType.Other => throw new NotSupportedException($"不支持的通讯方式：{type}"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// 检查指定通讯方式在当前环境下是否可用
    /// </summary>
    public bool IsTransportAvailable(CommunicationType type)
    {
        return type switch
        {
            CommunicationType.Usb => true, // 总是可用（取决于是否有 U 盘插入）
            CommunicationType.Rj45 => true, // 网络总是可用
            CommunicationType.Rs232 => System.IO.Ports.SerialPort.GetPortNames().Length > 0,
            CommunicationType.Rs485 => System.IO.Ports.SerialPort.GetPortNames().Length > 0,
            _ => false
        };
    }

    /// <summary>
    /// 获取当前环境下所有可用的通讯方式
    /// </summary>
    public IReadOnlyList<CommunicationType> GetAvailableTransports()
    {
        var available = new List<CommunicationType>
        {
            CommunicationType.Usb,
            CommunicationType.Rj45
        };

        var ports = System.IO.Ports.SerialPort.GetPortNames();
        if (ports.Length > 0)
        {
            available.Add(CommunicationType.Rs232);
            available.Add(CommunicationType.Rs485);
        }

        return available;
    }
}

/// <summary>
/// Modbus PLC 连接工厂实现
/// </summary>
public sealed class ModbusPlcConnectionFactory : IModbusPlcConnectionFactory
{
    /// <summary>
    /// 创建 Modbus PLC 连接
    /// </summary>
    /// <param name="protocol">传输模式："tcp" 或 "rtu"</param>
    public IModbusPlcConnection Create(string protocol = "tcp")
    {
        if (protocol != "tcp" && protocol != "rtu")
            throw new ArgumentException("协议必须是 'tcp' 或 'rtu'", nameof(protocol));

        return new ModbusPlcConnection(protocol);
    }
}
