using System.Collections.Concurrent;
using IsolationLeakage.App.Communication.Interfaces;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Communication.Results;
using IsolationLeakage.App.Models;
using Serilog;
using S7.Net;

namespace IsolationLeakage.App.Communication.Implementations;

/// <summary>
/// 西门子 S7 系列 PLC 连接（用于实时监视）
/// 支持 S7-200/S7-300/S7-400/S7-1200/S7-1500 等型号。
/// 地址格式：DB15.DBD0（数据块15，双字0）、DB15.DBW0（字）、DB15.DBB0（字节）
/// </summary>
public sealed class SiemensS7PlcConnection : IModbusPlcConnection
{
    private Plc? _plc;
    private bool _disposed;
    private string _ipAddress = string.Empty;
    private CpuType _cpuType = CpuType.S71200;
    private short _rack = 0;
    private short _slot = 1;

    /// <summary>
    /// 已解析的西门子地址缓存：地址字符串 → (数据块号, 起始字节, 数据类型)
    /// </summary>
    private readonly ConcurrentDictionary<string, (int DbNumber, int StartByte, VarType VarType)> _addressCache = new();

    public ConnectionStatus Status => _plc?.IsConnected == true ? ConnectionStatus.Online : ConnectionStatus.Offline;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 创建西门子 S7 PLC 连接
    /// </summary>
    /// <param name="cpuType">PLC 型号：S71200、S71500、S7300、S7400、S7200</param>
    public SiemensS7PlcConnection(string cpuType = "S71200")
    {
        _cpuType = cpuType.ToUpper() switch
        {
            "S71200" => CpuType.S71200,
            "S71500" => CpuType.S71500,
            "S7300" => CpuType.S7300,
            "S7400" => CpuType.S7400,
            "S7200" => CpuType.S7200,
            _ => CpuType.S71200
        };
    }

    /// <summary>
    /// 连接到 PLC
    /// </summary>
    /// <param name="ipAddress">PLC IP 地址</param>
    /// <param name="port">S7 协议默认端口 102，一般不需要修改</param>
    /// <param name="ct">取消令牌</param>
    public async Task<DeviceResult> ConnectAsync(string ipAddress, int port = 102, CancellationToken ct = default)
    {
        if (_disposed) return DeviceResult.Fail("连接已释放");

        try
        {
            _ipAddress = ipAddress;

            // 端口参数在 S7.Net 中实际由 CPU 类型决定，一般为 102
            // port 参数保留用于兼容性，实际不使用
            _plc = new Plc(_cpuType, ipAddress, _rack, _slot);

            // 使用同步 Open 方法（兼容旧版 S7.Net DLL）
            await Task.Run(() => _plc.Open(), ct);

            if (!_plc.IsConnected)
            {
                CleanupPlc();
                return DeviceResult.Fail($"无法连接到 PLC {ipAddress}:102");
            }

            OnStateChanged(ConnectionStatus.Offline, ConnectionStatus.Online, $"已连接西门子 PLC {ipAddress} ({_cpuType})");
            return DeviceResult.Success($"已连接西门子 PLC {ipAddress}:102 ({_cpuType})");
        }
        catch (Exception ex)
        {
            CleanupPlc();
            return DeviceResult.Fail($"连接 PLC 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 断开与 PLC 的连接
    /// </summary>
    public Task<DeviceResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (_plc == null)
            return Task.FromResult(DeviceResult.Fail("未连接"));

        try
        {
            _plc.Close();
            CleanupPlc();

            OnStateChanged(ConnectionStatus.Online, ConnectionStatus.Offline, "已断开 PLC 连接");
            return Task.FromResult(DeviceResult.Success("已断开 PLC 连接"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DeviceResult.Fail($"断开连接异常: {ex.Message}"));
        }
    }

    /// <summary>
    /// 从指定地址读取一个 Real (float) 值
    /// </summary>
    /// <param name="startAddress">西门子地址字符串，如 "DB15.DBD0" 或寄存器地址数字（兼容模式）</param>
    public async Task<DeviceResult<double>> ReadDoubleAsync(int startAddress, CancellationToken ct = default)
    {
        // 兼容模式：数字地址映射到默认数据块
        var siemensAddress = $"DB15.DBD{startAddress}";
        return await ReadDoubleAsync(siemensAddress, ct);
    }

    /// <summary>
    /// 从指定西门子地址读取一个 Real (float) 值
    /// </summary>
    /// <param name="siemensAddress">西门子地址，如 "DB15.DBD0"</param>
    public async Task<DeviceResult<double>> ReadDoubleAsync(string siemensAddress, CancellationToken ct = default)
    {
        if (_plc?.IsConnected != true)
            return DeviceResult<double>.Fail("PLC 未连接");

        try
        {
            var (dbNumber, startByte, varType) = ParseSiemensAddress(siemensAddress, VarType.Real);

            // 读取 Real 类型（4字节浮点数）- 使用同步方式兼容旧版 DLL
            var value = await Task.Run(() => _plc.Read(DataType.DataBlock, dbNumber, startByte, varType, 1), ct);

            if (value == null)
                return DeviceResult<double>.Fail($"读取地址 {siemensAddress} 失败: 返回空值");

            double result = Convert.ToDouble(value);
            return DeviceResult<double>.Success(result);
        }
        catch (Exception ex)
        {
            return DeviceResult<double>.Fail($"读取地址 {siemensAddress} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从指定地址读取一个 ushort (Word) 值
    /// </summary>
    /// <param name="startAddress">西门子地址字符串，如 "DB15.DBW0" 或寄存器地址数字（兼容模式）</param>
    public async Task<DeviceResult<ushort>> ReadUshortAsync(int startAddress, CancellationToken ct = default)
    {
        var siemensAddress = $"DB15.DBW{startAddress}";
        return await ReadUshortAsync(siemensAddress, ct);
    }

    /// <summary>
    /// 从指定西门子地址读取一个 ushort (Word) 值
    /// </summary>
    /// <param name="siemensAddress">西门子地址，如 "DB15.DBW0"</param>
    public async Task<DeviceResult<ushort>> ReadUshortAsync(string siemensAddress, CancellationToken ct = default)
    {
        if (_plc?.IsConnected != true)
            return DeviceResult<ushort>.Fail("PLC 未连接");

        try
        {
            var (dbNumber, startByte, varType) = ParseSiemensAddress(siemensAddress, VarType.Word);

            // 读取 Word 类型（2字节无符号整数）
            var value = await _plc.ReadAsync(DataType.DataBlock, dbNumber, startByte, varType, 1);

            if (value == null)
                return DeviceResult<ushort>.Fail($"读取地址 {siemensAddress} 失败: 返回空值");

            ushort result = Convert.ToUInt16(value);
            return DeviceResult<ushort>.Success(result);
        }
        catch (Exception ex)
        {
            return DeviceResult<ushort>.Fail($"读取地址 {siemensAddress} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从指定西门子地址读取一个 uint (DWord) 值
    /// </summary>
    /// <param name="siemensAddress">西门子地址，如 "DB15.DBD12"</param>
    public async Task<DeviceResult<uint>> ReadDWordAsync(string siemensAddress, CancellationToken ct = default)
    {
        if (_plc?.IsConnected != true)
            return DeviceResult<uint>.Fail("PLC 未连接");

        try
        {
            var (dbNumber, startByte, varType) = ParseSiemensAddress(siemensAddress, VarType.DWord);

            // 读取 DWord 类型（4字节无符号整数）
            var value = await _plc.ReadAsync(DataType.DataBlock, dbNumber, startByte, varType, 1);

            if (value == null)
                return DeviceResult<uint>.Fail($"读取地址 {siemensAddress} 失败: 返回空值");

            uint result = Convert.ToUInt32(value);
            return DeviceResult<uint>.Success(result);
        }
        catch (Exception ex)
        {
            return DeviceResult<uint>.Fail($"读取地址 {siemensAddress} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 批量读取多个寄存器（兼容 Modbus 接口）
    /// </summary>
    public async Task<DeviceResult<Dictionary<int, double>>> ReadMultipleAsync(
        IReadOnlyList<PlcRegisterRequest> requests, CancellationToken ct = default)
    {
        // 此方法保留用于兼容旧接口
        var result = new Dictionary<int, double>();

        foreach (var req in requests)
        {
            if (req.DataType == "ushort" || req.DataType == "word" || req.DataType == "int")
            {
                var ushortResult = await ReadUshortAsync(req.Address, ct);
                result[req.Address] = ushortResult.IsSuccess ? ushortResult.Data : double.NaN;
            }
            else
            {
                var doubleResult = await ReadDoubleAsync(req.Address, ct);
                result[req.Address] = doubleResult.IsSuccess ? doubleResult.Data : double.NaN;
            }
        }

        return DeviceResult<Dictionary<int, double>>.Success(result);
    }

    /// <summary>
    /// 批量读取多个西门子地址变量
    /// </summary>
    public async Task<DeviceResult<Dictionary<string, double>>> ReadMultipleBySiemensAddressAsync(
        IReadOnlyList<SiemensReadRequest> requests, CancellationToken ct = default)
    {
        if (_plc?.IsConnected != true)
            return DeviceResult<Dictionary<string, double>>.Fail("PLC 未连接");

        var result = new Dictionary<string, double>();

        try
        {
            foreach (var req in requests)
            {
                try
                {
                    var (dbNumber, startByte, varType) = ParseSiemensAddress(req.SiemensAddress, GetVarTypeFromDataType(req.DataType));

                    var value = await _plc.ReadAsync(DataType.DataBlock, dbNumber, startByte, varType, 1);

                    if (value != null)
                    {
                        result[req.SiemensAddress] = Convert.ToDouble(value);
                    }
                    else
                    {
                        result[req.SiemensAddress] = double.NaN;
                    }
                }
                catch
                {
                    result[req.SiemensAddress] = double.NaN;
                }
            }

            return DeviceResult<Dictionary<string, double>>.Success(result);
        }
        catch (Exception ex)
        {
            return DeviceResult<Dictionary<string, double>>.Fail($"批量读取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析西门子地址格式
    /// 支持格式：
    /// - DB15.DBD0 → 数据块15，双字0（Real/DWord）
    /// - DB15.DBW0 → 数据块15，字0（Word）
    /// - DB15.DBB0 → 数据块15，字节0（Byte）
    /// </summary>
    private (int DbNumber, int StartByte, VarType VarType) ParseSiemensAddress(string address, VarType defaultType)
    {
        if (_addressCache.TryGetValue(address, out var cached))
            return cached;

        // 解析格式：DB15.DBD0
        var parts = address.ToUpper().Split('.');
        if (parts.Length != 2)
            throw new ArgumentException($"无效的西门子地址格式: {address}，应为 DB15.DBD0 格式");

        // 解析数据块号：DB15 → 15
        if (!parts[0].StartsWith("DB") || !int.TryParse(parts[0].AsSpan(2), out var dbNumber))
            throw new ArgumentException($"无效的数据块号: {parts[0]}");

        // 解析偏移和类型：DBD0、DBW0、DBB0
        var offsetPart = parts[1];
        VarType varType;
        int startByte;

        if (offsetPart.StartsWith("DBD"))
        {
            // DBD = Double Word（4字节）→ Real 或 DWord
            varType = defaultType == VarType.Word ? VarType.DWord : defaultType;
            if (!int.TryParse(offsetPart.AsSpan(3), out startByte))
                throw new ArgumentException($"无效的偏移地址: {offsetPart}");
        }
        else if (offsetPart.StartsWith("DBW"))
        {
            // DBW = Word（2字节）
            varType = VarType.Word;
            if (!int.TryParse(offsetPart.AsSpan(3), out startByte))
                throw new ArgumentException($"无效的偏移地址: {offsetPart}");
        }
        else if (offsetPart.StartsWith("DBB"))
        {
            // DBB = Byte（1字节）
            varType = VarType.Byte;
            if (!int.TryParse(offsetPart.AsSpan(3), out startByte))
                throw new ArgumentException($"无效的偏移地址: {offsetPart}");
        }
        else if (int.TryParse(offsetPart, out startByte))
        {
            // 简洁格式：DB15.0（只有数字偏移），使用 defaultType 决定数据类型
            // Real/DWord 默认占4字节，Word占2字节，Byte占1字节
            varType = defaultType;
        }
        else
        {
            throw new ArgumentException($"不支持的地址类型: {offsetPart}，应为 DBD/DBW/DBB 或纯数字偏移");
        }

        var result = (dbNumber, startByte, varType);
        _addressCache.TryAdd(address, result);
        return result;
    }

    /// <summary>
    /// 根据数据类型字符串转换为 VarType
    /// </summary>
    private static VarType GetVarTypeFromDataType(string dataType)
    {
        return dataType.ToLower() switch
        {
            "real" or "float" or "double" => VarType.Real,
            "word" or "ushort" or "int" => VarType.Word,
            "dword" or "uint" => VarType.DWord,
            "byte" => VarType.Byte,
            _ => VarType.Real
        };
    }

    /// <summary>
    /// 清理 PLC 资源
    /// </summary>
    private void CleanupPlc()
    {
        try { _plc?.Close(); }
        catch (Exception ex) { Log.Warning(ex, "关闭 PLC 连接时发生警告"); }
        _plc = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            var wasConnected = _plc?.IsConnected == true;
            CleanupPlc();

            if (wasConnected)
            {
                OnStateChanged(ConnectionStatus.Online, ConnectionStatus.Offline, "[S7] 已断开 PLC");
            }
        }
    }

    private void OnStateChanged(ConnectionStatus oldStatus, ConnectionStatus newStatus, string message)
    {
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Message = message
        });
    }
}

/// <summary>
/// 西门子地址读取请求
/// </summary>
public sealed class SiemensReadRequest
{
    /// <summary>西门子地址，如 "DB15.DBD0"</summary>
    public string SiemensAddress { get; init; } = string.Empty;

    /// <summary>数据类型：real、word、dword、byte 等</summary>
    public string DataType { get; init; } = "real";

    public override string ToString() => $"{SiemensAddress}({DataType})";
}
