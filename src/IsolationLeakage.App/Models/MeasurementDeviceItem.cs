namespace IsolationLeakage.App.Models;

public sealed class MeasurementDeviceItem
{
    public string DeviceCode { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string SerialNumber { get; set; } = string.Empty;

    public string PrimaryCommunication { get; set; } = string.Empty;

    public string EnabledStatus { get; set; } = "\u542f\u7528";

    public string RecentConnectionStatus { get; set; } = "\u672a\u540c\u6b65";

    public string LastSyncTime { get; set; } = "-";

    public string LastUploadTime { get; set; } = "-";

    public int UploadCount { get; set; }

    public string LastUploadResult { get; set; } = "-";

    public string Remark { get; set; } = string.Empty;
}
