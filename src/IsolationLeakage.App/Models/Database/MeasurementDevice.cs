using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 测量装置表
/// </summary>
[Table("MeasurementDevices")]
public sealed class MeasurementDevice : INotifyPropertyChanged
{
    private string _deviceCode = string.Empty;
    private string _deviceName = string.Empty;
    private string? _ip;
    private string? _serialNumber;
    private CommunicationType _primaryCommunication = CommunicationType.Rj45;
    private EnabledStatus _enabledStatus = EnabledStatus.Enabled;
    private ConnectionStatus _connectionStatus = ConnectionStatus.Unknown;
    private DateTime? _lastSyncTime;
    private DateTime? _lastUploadTime;
    private int _uploadCount = 0;
    private TestResult? _lastUploadResult;
    private string? _remark;
    private DateTime _createdAt = DateTime.Now;
    private DateTime? _updatedAt;

    [Key]
    [MaxLength(50)]
    public string DeviceCode
    {
        get => _deviceCode;
        set => SetProperty(ref _deviceCode, value);
    }

    [Required]
    [MaxLength(200)]
    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    [MaxLength(100)]
    public string? Ip
    {
        get => _ip;
        set => SetProperty(ref _ip, value);
    }

    [MaxLength(100)]
    public string? SerialNumber
    {
        get => _serialNumber;
        set => SetProperty(ref _serialNumber, value);
    }

    public CommunicationType PrimaryCommunication
    {
        get => _primaryCommunication;
        set
        {
            if (SetProperty(ref _primaryCommunication, value))
            {
                OnPropertyChanged(nameof(PrimaryCommunicationText));
            }
        }
    }

    public EnabledStatus EnabledStatus
    {
        get => _enabledStatus;
        set
        {
            if (SetProperty(ref _enabledStatus, value))
            {
                OnPropertyChanged(nameof(EnabledStatusText));
            }
        }
    }

    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusText));
            }
        }
    }

    public string EnabledStatusText => EnabledStatus.ToText();
    public string ConnectionStatusText => ConnectionStatus.ToText();
    public string PrimaryCommunicationText => PrimaryCommunication.ToText();
    public string LastUploadResultText => LastUploadResult.HasValue ? LastUploadResult.Value.ToText() : "-";

    public DateTime? LastSyncTime
    {
        get => _lastSyncTime;
        set => SetProperty(ref _lastSyncTime, value);
    }

    public DateTime? LastUploadTime
    {
        get => _lastUploadTime;
        set => SetProperty(ref _lastUploadTime, value);
    }

    public int UploadCount
    {
        get => _uploadCount;
        set => SetProperty(ref _uploadCount, value);
    }

    public TestResult? LastUploadResult
    {
        get => _lastUploadResult;
        set
        {
            if (SetProperty(ref _lastUploadResult, value))
            {
                OnPropertyChanged(nameof(LastUploadResultText));
            }
        }
    }

    [MaxLength(1000)]
    public string? Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetProperty(ref _createdAt, value);
    }

    public DateTime? UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    // 导航属性：该装置上传的试验记录
    public ICollection<TestRecord> TestRecords { get; set; } = [];

    // INotifyPropertyChanged 实现
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
