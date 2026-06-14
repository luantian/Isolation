using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Communication.Interfaces;

/// <summary>
/// 设备连接工厂接口
/// </summary>
public interface IConnectionFactory
{
    /// <summary>创建指定通讯方式的连接实例</summary>
    IDeviceConnection Create(CommunicationType type);

    /// <summary>检查指定通讯方式在当前环境下是否可用</summary>
    bool IsTransportAvailable(CommunicationType type);

    /// <summary>获取当前环境下所有可用的通讯方式</summary>
    IReadOnlyList<CommunicationType> GetAvailableTransports();
}
