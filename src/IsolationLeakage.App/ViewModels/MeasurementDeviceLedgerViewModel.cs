using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
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
    private int _messageType; // 0=普通, 1=成功, 2=错误
    private string _deviceCode = string.Empty;
    private string _deviceName = string.Empty;
    private string _model = string.Empty;
    private string _serialNumber = string.Empty;
    private string _remark = string.Empty;
    private CommunicationType _communicationType = CommunicationType.Usb;
    private bool _isNewMode;
    private CancellationTokenSource? _messageClearCts;

    public MeasurementDeviceLedgerViewModel()
    {
        FilteredDevices = new ObservableCollection<MeasurementDevice>();
        // 使用枚举值而不是字符串，解决绑定问题
        CommunicationOptions = Enum.GetValues<CommunicationType>().Cast<CommunicationType>().ToList();
        CommunicationFilterOptions = new List<string> { "全部", "USB", "RJ45", "RS232", "RS485" };
        EnabledFilterOptions = new List<string> { "全部", "启用", "停用" };
        _communicationFilter = "全部";
        _enabledFilter = "全部";

        // 命令初始化（只初始化一次）
        NewDeviceCommand = new RelayCommand(StartNew);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !string.IsNullOrWhiteSpace(DeviceName));
        EnableCommand = new RelayCommand(() => _ = EnableSelectedAsync(), () => SelectedDevice != null && SelectedDevice.EnabledStatus == EnabledStatus.Disabled);
        DisableCommand = new RelayCommand(() => _ = DisableSelectedAsync(), () => SelectedDevice != null && SelectedDevice.EnabledStatus == EnabledStatus.Enabled);
        DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => SelectedDevice != null && PermissionGuard.Can(Perms.DeviceAdd));
        QueryCommand = new RelayCommand(() => _ = ApplyQueryAsync());

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
                SetMessage($"初始化加载失败：{ex.Message}", 2);
            }
        }
    }

    public ObservableCollection<MeasurementDevice> FilteredDevices { get; }

    public IReadOnlyList<CommunicationType> CommunicationOptions { get; }

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
        set
        {
            if (SetProperty(ref _deviceCode, value))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (SetProperty(ref _deviceName, value))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
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

    public string Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    public CommunicationType PrimaryCommunication
    {
        get => _communicationType;
        set => SetProperty(ref _communicationType, value);
    }

    /// <summary>
    /// 消息类型：0=普通, 1=成功, 2=错误
    /// </summary>
    public int MessageType
    {
        get => _messageType;
        private set => SetProperty(ref _messageType, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>
    /// 是否有消息显示
    /// </summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>
    /// 设置消息（带自动清除）
    /// </summary>
    private void SetMessage(string message, int type = 0)
    {
        // 取消之前的清除定时器
        _messageClearCts?.Cancel();
        _messageClearCts?.Dispose();

        Message = message;
        MessageType = type;
        OnPropertyChanged(nameof(HasMessage));

        // 3秒后自动清除
        if (!string.IsNullOrEmpty(message))
        {
            _messageClearCts = new CancellationTokenSource();
            _ = Task.Delay(3000, _messageClearCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Message = string.Empty;
                    MessageType = 0;
                    OnPropertyChanged(nameof(HasMessage));
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
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
                NotifyCommandCanExecuteChanged();
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

    /// <summary>
    /// 当前选中的装置是否有上传记录（用于删除保护）
    /// </summary>
    public bool HasUploadRecords => SelectedDevice?.UploadCount > 0;

    // 命令（构造函数中初始化，只创建一次）
    public RelayCommand NewDeviceCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand EnableCommand { get; }
    public RelayCommand DisableCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand QueryCommand { get; }

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
            SetMessage($"已从数据库加载 {FilteredDevices.Count} 台装置", 0);
        }
        catch (Exception ex)
        {
            SetMessage($"加载数据失败：{ex.Message}", 2);
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

            SetMessage($"查询完成，共 {FilteredDevices.Count} 条记录", 0);
        }
        catch (Exception ex)
        {
            SetMessage($"查询失败：{ex.Message}", 2);
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
        Remark = string.Empty;
        PrimaryCommunication = CommunicationType.Usb;
        NotifyCommandCanExecuteChanged();
        SetMessage("正在新增测量装置，保存后将写入数据库", 0);
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
                // 先检查编码是否已存在
                var existingDevice = await context.MeasurementDevices.FindAsync(DeviceCode.Trim());
                if (existingDevice != null)
                {
                    SetMessage($"❌ 装置编号 {DeviceCode.Trim()} 已存在，请修改后重试", 2);
                    return;
                }

                // 新增装置到数据库
                var newDevice = new MeasurementDevice
                {
                    DeviceCode = DeviceCode.Trim(),
                    DeviceName = DeviceName.Trim(),
                    Model = Model.Trim(),
                    SerialNumber = SerialNumber.Trim(),
                    Remark = Remark.Trim(),
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
                SetMessage($"✅ 已新增装置并保存到数据库：{newDevice.DeviceCode}", 1);
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
                    device.Remark = Remark.Trim();
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
                    SelectedDevice.Remark = device.Remark;
                    SelectedDevice.PrimaryCommunication = device.PrimaryCommunication;
                    SelectedDevice.UpdatedAt = device.UpdatedAt;

                    SetMessage($"✅ 已保存修改到数据库：{SelectedDevice.DeviceCode}", 1);
                }
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 保存失败：{ex.Message}", 2);
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
                NotifyCommandCanExecuteChanged();
                SetMessage($"✅ 已启用装置：{SelectedDevice.DeviceCode}", 1);
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 操作失败：{ex.Message}", 2);
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
                NotifyCommandCanExecuteChanged();
                SetMessage($"✅ 已停用装置：{SelectedDevice.DeviceCode}", 1);
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 操作失败：{ex.Message}", 2);
        }
    }

    /// <summary>
    /// 删除选中装置（带删除保护：有上传记录的装置不能删除）
    /// </summary>
    public async Task DeleteSelectedAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            // 删除保护：有上传记录的装置不能删除
            if (HasUploadRecords)
            {
                SetMessage($"❌ 该装置已有 {SelectedDevice.UploadCount} 条上传记录，不允许删除", 2);
                return;
            }

            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
            if (device != null)
            {
                context.MeasurementDevices.Remove(device);
                await context.SaveChangesAsync();

                // 记录操作日志
                await logService.LogAsync("删除测量装置", currentUser,
                    $"删除测量装置【{device.DeviceName}】({device.DeviceCode})", "Success");

                // 从内存中移除
                var codeToRemove = SelectedDevice.DeviceCode;
                FilteredDevices.Remove(SelectedDevice);

                // 选中下一个
                SelectedDevice = FilteredDevices.FirstOrDefault();

                SetMessage($"✅ 已删除装置：{codeToRemove}", 1);
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 删除失败：{ex.Message}", 2);
        }
    }

    private bool ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(DeviceCode))
        {
            SetMessage("装置编号不能为空", 2);
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            SetMessage("装置名称不能为空", 2);
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
        Remark = SelectedDevice.Remark ?? string.Empty;
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

    private void NotifyCommandCanExecuteChanged()
    {
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
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
