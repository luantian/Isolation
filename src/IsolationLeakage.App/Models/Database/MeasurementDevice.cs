using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 测量装置表
/// </summary>
[Table("MeasurementDevices")]
public sealed class MeasurementDevice
{
    [Key]
    [MaxLength(50)]
    public string DeviceCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Model { get; set; }

    [MaxLength(100)]
    public string? SerialNumber { get; set; }

    public CommunicationType PrimaryCommunication { get; set; } = CommunicationType.Usb;

    public EnabledStatus EnabledStatus { get; set; } = EnabledStatus.Enabled;

    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Unknown;

    public string EnabledStatusText => EnabledStatus.ToText();
    public string ConnectionStatusText => ConnectionStatus.ToText();
    public string PrimaryCommunicationText => PrimaryCommunication.ToText();
    public string LastUploadResultText => LastUploadResult.HasValue ? LastUploadResult.Value.ToText() : "-";

    public DateTime? LastSyncTime { get; set; }

    public DateTime? LastUploadTime { get; set; }

    public int UploadCount { get; set; } = 0;

    public TestResult? LastUploadResult { get; set; }

    [MaxLength(1000)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：该装置上传的试验记录
    public ICollection<TestRecord> TestRecords { get; set; } = [];
}
