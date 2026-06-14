namespace IsolationLeakage.App.Communication.Results;

/// <summary>
/// 设备操作统一结果
/// </summary>
public class DeviceResult
{
    public bool IsSuccess { get; init; }
    public string Error { get; init; } = string.Empty;

    protected DeviceResult(bool isSuccess, string error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static DeviceResult Success() => new(true, string.Empty);
    public static DeviceResult Success(string message) => new(true, message);
    public static DeviceResult Fail(string error) => new(false, error);
}

/// <summary>
/// 设备操作统一结果（带数据）
/// </summary>
public sealed class DeviceResult<T> : DeviceResult
{
    public T? Data { get; init; }

    private DeviceResult(bool isSuccess, string error, T? data)
        : base(isSuccess, error)
    {
        Data = data;
    }

    public static DeviceResult<T> Success(T data) => new(true, string.Empty, data);
    public static new DeviceResult<T> Fail(string error) => new(false, error, default);
}
