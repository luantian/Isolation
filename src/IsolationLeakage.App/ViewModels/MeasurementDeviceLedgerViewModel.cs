using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 测量装置台账视图模型
/// </summary>
public sealed class MeasurementDeviceLedgerViewModel : ViewModelBase
{
    private MeasurementDevice? _selectedDevice;
    private string _searchText = string.Empty;
    private string _message = string.Empty;
    private string _deviceCode = string.Empty;
    private string _deviceName = string.Empty;
    private string _model = string.Empty;
    private string _serialNumber = string.Empty;
    private CommunicationType _communicationType = CommunicationType.Usb;
    private bool _isNewMode;

    public MeasurementDeviceLedgerViewModel()
    {
        FilteredDevices = new ObservableCollection<MeasurementDevice>();
        CommunicationOptions = new List<string> { "USB", "RJ45", "RS232", "RS485" };
        CommunicationFilterOptions = new List<string> { "全部", "USB", "RJ45", "RS232", "RS485" };
        EnabledFilterOptions = new List<string> { "全部", "启用", "停用" };
        _communicationFilter = "全部";
        _enabledFilter = "全部";

        // 从数据库加载数据
        _ = SafeLoadAsync();

        async Task SafeLoadAsync()
        {
            try
            {
                await LoadDataAsync();
            }
            catch (Exception ex)
            {
                Message = $"初始化加载失败：{ex.Message}";
            }
        }
    }

    public ObservableCollection<MeasurementDevice> FilteredDevices { get; }

    public IReadOnlyList<string> CommunicationOptions { get; }

    public IReadOnlyList<string> CommunicationFilterOptions { get; }

    public IReadOnlyList<string> EnabledFilterOptions { get; }

    private string _communicationFilter;
    public string CommunicationFilter
    {
        get => _communicationFilter;
        set
        {
            if (SetProperty(ref _communicationFilter, value))
            {
                _ = ApplyQueryAsync();
            }
        }
    }

    private string _enabledFilter;
    public string EnabledFilter
    {
        get => _enabledFilter;
        set
        {
            if (SetProperty(ref _enabledFilter, value))
            {
                _ = ApplyQueryAsync();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool IsNewMode
    {
        get => _isNewMode;
        private set
        {
            if (SetProperty(ref _isNewMode, value))
            {
                OnPropertyChanged(nameof(IsExistingMode));
            }
        }
    }

    public bool IsExistingMode => !IsNewMode && SelectedDevice != null;

    public string DeviceCode
    {
        get => _deviceCode;
        set => SetProperty(ref _deviceCode, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public string SerialNumber
    {
        get => _serialNumber;
        set => SetProperty(ref _serialNumber, value);
    }

    public CommunicationType PrimaryCommunication
    {
        get => _communicationType;
        set => SetProperty(ref _communicationType, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public MeasurementDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                LoadSelectedDevice();
                OnPropertyChanged(nameof(IsExistingMode));
                NotifyReadOnlyStatusChanged();
            }
        }
    }

    public string EnabledStatusText => SelectedDevice?.EnabledStatus switch
    {
        EnabledStatus.Enabled => "启用",
        EnabledStatus.Disabled => "停用",
        _ => "-"
    };

    public string ConnectionStatusText => SelectedDevice?.ConnectionStatus switch
    {
        ConnectionStatus.Online => "在线",
        ConnectionStatus.Offline => "离线",
        _ => "未同步"
    };

    public string LastSyncTimeText => SelectedDevice?.LastSyncTime?.ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string LastUploadTimeText => SelectedDevice?.LastUploadTime?.ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string UploadCountText => SelectedDevice?.UploadCount.ToString() ?? "0";

    public string LastUploadResultText => SelectedDevice?.LastUploadResult switch
    {
        TestResult.Pass => "合格",
        TestResult.Fail => "不合格",
        _ => "-"
    };

    public IRelayCommand NewDeviceCommand => new RelayCommand(StartNew);
    public IRelayCommand SaveCommand => new RelayCommand(() => _ = SaveAsync());
    public IRelayCommand EnableCommand => new RelayCommand(() => _ = EnableSelectedAsync());
    public IRelayCommand DisableCommand => new RelayCommand(() => _ = DisableSelectedAsync());
    public IRelayCommand QueryCommand => new RelayCommand(() => _ = ApplyQueryAsync());

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var devices = await context.MeasurementDevices.ToListAsync();

            FilteredDevices.Clear();
            foreach (var device in devices)
            {
                FilteredDevices.Add(device);
            }

            SelectedDevice = FilteredDevices.FirstOrDefault();
            Message = $"已从数据库加载 {FilteredDevices.Count} 台装置";
        }
        catch (Exception ex)
        {
            Message = $"加载数据失败：{ex.Message}";
        }
    }

    public async Task ApplyQueryAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var query = context.MeasurementDevices.AsQueryable();

            if (CommunicationFilter != "全部")
            {
                var commType = MapCommunicationType(CommunicationFilter);
                query = query.Where(d => d.PrimaryCommunication == commType);
            }

            if (EnabledFilter != "全部")
            {
                var status = EnabledFilter == "启用" ? EnabledStatus.Enabled : EnabledStatus.Disabled;
                query = query.Where(d => d.EnabledStatus == status);
            }

            var keyword = SearchText.Trim();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(d =>
                    d.DeviceCode.Contains(keyword) ||
                    d.DeviceName.Contains(keyword) ||
                    (d.Model != null && d.Model.Contains(keyword)) ||
                    (d.SerialNumber != null && d.SerialNumber.Contains(keyword)));
            }

            var results = await query.ToListAsync();
            var previousCode = SelectedDevice?.DeviceCode;

            FilteredDevices.Clear();
            foreach (var device in results)
            {
                FilteredDevices.Add(device);
            }

            SelectedDevice = FilteredDevices.FirstOrDefault(d => d.DeviceCode == previousCode)
                           ?? FilteredDevices.FirstOrDefault();

            Message = $"查询完成，共 {FilteredDevices.Count} 条记录";
        }
        catch (Exception ex)
        {
            Message = $"查询失败：{ex.Message}";
        }
    }

    public void StartNew()
    {
        IsNewMode = true;
        SelectedDevice = null;
        DeviceCode = $"DEV-{DateTime.Now:yyyyMMddHHmm}";
        DeviceName = string.Empty;
        Model = string.Empty;
        SerialNumber = string.Empty;
        PrimaryCommunication = CommunicationType.Usb;
        Message = "正在新增测量装置，保存后将写入数据库";
    }

    public async Task SaveAsync()
    {
        if (!ValidateEditor())
        {
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            if (IsNewMode)
            {
                // 新增装置到数据库
                var newDevice = new MeasurementDevice
                {
                    DeviceCode = DeviceCode.Trim(),
                    DeviceName = DeviceName.Trim(),
                    Model = Model.Trim(),
                    SerialNumber = SerialNumber.Trim(),
                    PrimaryCommunication = PrimaryCommunication,
                    EnabledStatus = EnabledStatus.Enabled,
                    ConnectionStatus = ConnectionStatus.Offline,
                    CreatedAt = DateTime.Now
                };

                context.MeasurementDevices.Add(newDevice);
                await context.SaveChangesAsync();

                // 记录操作日志
                await logService.LogAsync("创建测量装置", currentUser,
                    $"新增测量装置【{newDevice.DeviceName}】({newDevice.DeviceCode})", "Success");

                FilteredDevices.Add(newDevice);
                IsNewMode = false;
                SelectedDevice = newDevice;
                Message = $"✅ 已新增装置并保存到数据库：{newDevice.DeviceCode}";
            }
            else if (SelectedDevice != null)
            {
                // 更新数据库中的装置
                var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
                if (device != null)
                {
                    device.DeviceName = DeviceName.Trim();
                    device.Model = Model.Trim();
                    device.SerialNumber = SerialNumber.Trim();
                    device.PrimaryCommunication = PrimaryCommunication;
                    device.UpdatedAt = DateTime.Now;

                    await context.SaveChangesAsync();

                    // 记录操作日志
                    await logService.LogAsync("修改测量装置", currentUser,
                        $"修改测量装置【{device.DeviceName}】({device.DeviceCode})", "Success");

                    // 更新内存集合中的引用
                    SelectedDevice.DeviceName = device.DeviceName;
                    SelectedDevice.Model = device.Model;
                    SelectedDevice.SerialNumber = device.SerialNumber;
                    SelectedDevice.PrimaryCommunication = device.PrimaryCommunication;
                    SelectedDevice.UpdatedAt = device.UpdatedAt;

                    Message = $"✅ 已保存修改到数据库：{SelectedDevice.DeviceCode}";
                }
            }
        }
        catch (Exception ex)
        {
            Message = $"❌ 保存失败：{ex.Message}";
        }
    }

    public async Task EnableSelectedAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
            if (device != null)
            {
                device.EnabledStatus = EnabledStatus.Enabled;
                device.UpdatedAt = DateTime.Now;
                await context.SaveChangesAsync();

                SelectedDevice.EnabledStatus = EnabledStatus.Enabled;
                SelectedDevice.UpdatedAt = device.UpdatedAt;
                NotifyReadOnlyStatusChanged();
                Message = $"✅ 已启用装置：{SelectedDevice.DeviceCode}";
            }
        }
        catch (Exception ex)
        {
            Message = $"❌ 操作失败：{ex.Message}";
        }
    }

    public async Task DisableSelectedAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
            if (device != null)
            {
                device.EnabledStatus = EnabledStatus.Disabled;
                device.UpdatedAt = DateTime.Now;
                await context.SaveChangesAsync();

                SelectedDevice.EnabledStatus = EnabledStatus.Disabled;
                SelectedDevice.UpdatedAt = device.UpdatedAt;
                NotifyReadOnlyStatusChanged();
                Message = $"✅ 已停用装置：{SelectedDevice.DeviceCode}";
            }
        }
        catch (Exception ex)
        {
            Message = $"❌ 操作失败：{ex.Message}";
        }
    }

    private bool ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(DeviceCode))
        {
            Message = "装置编号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            Message = "装置名称不能为空";
            return false;
        }

        return true;
    }

    private void LoadSelectedDevice()
    {
        if (SelectedDevice == null)
        {
            return;
        }

        IsNewMode = false;
        DeviceCode = SelectedDevice.DeviceCode;
        DeviceName = SelectedDevice.DeviceName;
        Model = SelectedDevice.Model ?? string.Empty;
        SerialNumber = SelectedDevice.SerialNumber ?? string.Empty;
        PrimaryCommunication = SelectedDevice.PrimaryCommunication;
    }

    private void NotifyReadOnlyStatusChanged()
    {
        OnPropertyChanged(nameof(EnabledStatusText));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(LastSyncTimeText));
        OnPropertyChanged(nameof(LastUploadTimeText));
        OnPropertyChanged(nameof(UploadCountText));
        OnPropertyChanged(nameof(LastUploadResultText));
    }

    private static CommunicationType MapCommunicationType(string display)
    {
        return display switch
        {
            "USB" => CommunicationType.Usb,
            "RJ45" => CommunicationType.Rj45,
            "RS232" => CommunicationType.Rs232,
            "RS485" => CommunicationType.Rs485,
            _ => CommunicationType.Usb
        };
    }
}
