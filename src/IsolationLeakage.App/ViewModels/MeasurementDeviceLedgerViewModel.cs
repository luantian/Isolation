using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 测量装置台账视图模型（弹窗模式）
/// </summary>
public sealed class MeasurementDeviceLedgerViewModel : ViewModelBase
{
    private MeasurementDevice? _selectedDevice;
    private string _searchText = string.Empty;
    private string _message = string.Empty;
    private int _messageType;
    private CancellationTokenSource? _messageClearCts;
    private string _communicationFilter = "全部";
    private string _enabledFilter = "全部";

    public MeasurementDeviceLedgerViewModel()
    {
        FilteredDevices = new ObservableCollection<MeasurementDevice>();
        CommunicationFilterOptions = new List<string> { "全部", "USB", "RJ45", "RS232", "RS485" };
        EnabledFilterOptions = new List<string> { "全部", "启用", "停用" };

        AddDeviceCommand = new RelayCommand(() => _ = ShowAddDeviceDialogAsync(), () => PermissionGuard.Can(Perms.DeviceAdd));
        EditDeviceCommand = new RelayCommand(() => _ = ShowEditDeviceDialogAsync(), () => SelectedDevice != null && PermissionGuard.Can(Perms.DeviceAdd));
        DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => SelectedDevice != null && PermissionGuard.Can(Perms.DeviceDelete));
        QueryCommand = new RelayCommand(() => _ = ApplyQueryAsync());

        _ = SafeLoadAsync();

        async Task SafeLoadAsync()
        {
            try { await LoadDataAsync(); }
            catch (Exception ex) { SetMessage($"初始化加载失败：{ex.Message}", 2); }
        }
    }

    public ObservableCollection<MeasurementDevice> FilteredDevices { get; }
    public IReadOnlyList<string> CommunicationFilterOptions { get; }
    public IReadOnlyList<string> EnabledFilterOptions { get; }

    public string CommunicationFilter
    {
        get => _communicationFilter;
        set { if (SetProperty(ref _communicationFilter, value)) _ = ApplyQueryAsync(); }
    }

    public string EnabledFilter
    {
        get => _enabledFilter;
        set { if (SetProperty(ref _enabledFilter, value)) _ = ApplyQueryAsync(); }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    // ── 消息系统 ──

    public int MessageType { get => _messageType; private set => SetProperty(ref _messageType, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    private void SetMessage(string message, int type = 0)
    {
        _messageClearCts?.Cancel();
        _messageClearCts?.Dispose();
        Message = message;
        MessageType = type;
        OnPropertyChanged(nameof(HasMessage));
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

    // ── 选中装置 ──

    public MeasurementDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                NotifyReadOnlyStatusChanged();
                ((RelayCommand)EditDeviceCommand).NotifyCanExecuteChanged();
                ((RelayCommand)DeleteCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedDevice));
            }
        }
    }

    public bool HasSelectedDevice => SelectedDevice != null;

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

    public string LastSyncTimeText => SelectedDevice?.LastSyncTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string LastUploadTimeText => SelectedDevice?.LastUploadTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string UploadCountText => SelectedDevice?.UploadCount.ToString() ?? "0";

    public string LastUploadResultText => SelectedDevice?.LastUploadResult switch
    {
        TestResult.Pass => "合格",
        TestResult.Fail => "不合格",
        _ => "-"
    };

    // ── 命令 ──

    public RelayCommand AddDeviceCommand { get; }
    public RelayCommand EditDeviceCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand QueryCommand { get; }

    // ── 新增 ──

    private async Task ShowAddDeviceDialogAsync()
    {
        var newDevice = new MeasurementDevice
        {
            DeviceCode = $"DEV-{DateTime.Now:yyyyMMddHHmm}",
            DeviceName = string.Empty,
            Ip = string.Empty,
            SerialNumber = string.Empty,
            Remark = string.Empty,
            PrimaryCommunication = CommunicationType.Usb,
            EnabledStatus = EnabledStatus.Enabled,
            ConnectionStatus = ConnectionStatus.Offline,
            CreatedAt = DateTime.Now
        };

        var dialog = new Views.MeasurementDeviceEditDialog(newDevice, false)
        {
            Title = "新增装置",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = UserSession.Current?.User.UserName ?? "system";

                if (await context.MeasurementDevices.AnyAsync(d => d.DeviceCode == newDevice.DeviceCode))
                {
                    MessageBox.Show("装置编号已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                context.MeasurementDevices.Add(newDevice);
                await context.SaveChangesAsync();

                await logService.LogAsync("创建测量装置", currentUser,
                    $"新增测量装置【{newDevice.DeviceName}】({newDevice.DeviceCode})", "Success");

                FilteredDevices.Add(newDevice);
                SelectedDevice = newDevice;
                SetMessage($"✅ 已新增装置并保存到数据库：{newDevice.DeviceCode}", 1);
            }
            catch (Exception ex)
            {
                SetMessage($"❌ 新增装置失败：{ex.Message}", 2);
            }
        }
    }

    // ── 编辑 ──

    private async Task ShowEditDeviceDialogAsync()
    {
        if (SelectedDevice == null) return;

        var editDevice = new MeasurementDevice
        {
            DeviceCode = SelectedDevice.DeviceCode,
            DeviceName = SelectedDevice.DeviceName,
            Ip = SelectedDevice.Ip ?? string.Empty,
            SerialNumber = SelectedDevice.SerialNumber ?? string.Empty,
            Remark = SelectedDevice.Remark ?? string.Empty,
            PrimaryCommunication = SelectedDevice.PrimaryCommunication,
            EnabledStatus = SelectedDevice.EnabledStatus,
            ConnectionStatus = SelectedDevice.ConnectionStatus,
            LastSyncTime = SelectedDevice.LastSyncTime,
            LastUploadTime = SelectedDevice.LastUploadTime,
            UploadCount = SelectedDevice.UploadCount,
            LastUploadResult = SelectedDevice.LastUploadResult,
            CreatedAt = SelectedDevice.CreatedAt
        };

        var dialog = new Views.MeasurementDeviceEditDialog(editDevice, true)
        {
            Title = "编辑装置",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = UserSession.Current?.User.UserName ?? "system";

                var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
                if (device == null)
                {
                    MessageBox.Show("装置在数据库中不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                device.DeviceName = editDevice.DeviceName;
                device.Ip = editDevice.Ip;
                device.SerialNumber = editDevice.SerialNumber;
                device.Remark = editDevice.Remark;
                device.PrimaryCommunication = editDevice.PrimaryCommunication;
                device.EnabledStatus = editDevice.EnabledStatus;
                device.UpdatedAt = DateTime.Now;

                await context.SaveChangesAsync();

                await logService.LogAsync("修改测量装置", currentUser,
                    $"修改测量装置【{device.DeviceName}】({device.DeviceCode})", "Success");

                // 更新 UI
                SelectedDevice.DeviceName = device.DeviceName;
                SelectedDevice.Ip = device.Ip;
                SelectedDevice.SerialNumber = device.SerialNumber;
                SelectedDevice.Remark = device.Remark;
                SelectedDevice.PrimaryCommunication = device.PrimaryCommunication;
                SelectedDevice.EnabledStatus = device.EnabledStatus;
                SelectedDevice.UpdatedAt = device.UpdatedAt;
                NotifyReadOnlyStatusChanged();

                SetMessage($"✅ 已保存装置修改：{device.DeviceCode}", 1);
            }
            catch (Exception ex)
            {
                SetMessage($"❌ 修改装置失败：{ex.Message}", 2);
            }
        }
    }

    // ── 删除 ──

    private async Task DeleteSelectedAsync()
    {
        if (SelectedDevice == null) return;

        if (SelectedDevice.UploadCount > 0)
        {
            SetMessage($"❌ 该装置已有 {SelectedDevice.UploadCount} 条上传记录，不允许删除", 2);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定要删除装置【{SelectedDevice.DeviceName}】({SelectedDevice.DeviceCode}) 吗？\n\n此操作不可恢复！",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = UserSession.Current?.User.UserName ?? "system";

            var device = await context.MeasurementDevices.FindAsync(SelectedDevice.DeviceCode);
            if (device != null)
            {
                context.MeasurementDevices.Remove(device);
                await context.SaveChangesAsync();

                await logService.LogAsync("删除测量装置", currentUser,
                    $"删除测量装置【{device.DeviceName}】({device.DeviceCode})", "Success");

                var codeToRemove = SelectedDevice.DeviceCode;
                FilteredDevices.Remove(SelectedDevice);
                SelectedDevice = FilteredDevices.FirstOrDefault();

                SetMessage($"✅ 已删除装置：{codeToRemove}", 1);
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 删除失败：{ex.Message}", 2);
        }
    }

    // ── 数据加载与查询 ──

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var devices = await context.MeasurementDevices.ToListAsync();

            FilteredDevices.Clear();
            foreach (var device in devices) FilteredDevices.Add(device);

            SelectedDevice = FilteredDevices.FirstOrDefault();
            SetMessage($"已从数据库加载 {FilteredDevices.Count} 台装置", 0);
        }
        catch (Exception ex)
        {
            SetMessage($"加载数据失败：{ex.Message}", 2);
        }
    }

    private async Task ApplyQueryAsync()
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
                    (d.Ip != null && d.Ip.Contains(keyword)) ||
                    (d.SerialNumber != null && d.SerialNumber.Contains(keyword)));
            }

            var results = await query.ToListAsync();
            var previousCode = SelectedDevice?.DeviceCode;

            FilteredDevices.Clear();
            foreach (var device in results) FilteredDevices.Add(device);

            SelectedDevice = FilteredDevices.FirstOrDefault(d => d.DeviceCode == previousCode)
                           ?? FilteredDevices.FirstOrDefault();

            SetMessage($"查询完成，共 {FilteredDevices.Count} 条记录", 0);
        }
        catch (Exception ex)
        {
            SetMessage($"查询失败：{ex.Message}", 2);
        }
    }

    // ── 辅助方法 ──

    private void NotifyReadOnlyStatusChanged()
    {
        OnPropertyChanged(nameof(EnabledStatusText));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(LastSyncTimeText));
        OnPropertyChanged(nameof(LastUploadTimeText));
        OnPropertyChanged(nameof(UploadCountText));
        OnPropertyChanged(nameof(LastUploadResultText));
    }

    private static CommunicationType MapCommunicationType(string display) => display switch
    {
        "USB" => CommunicationType.Usb,
        "RJ45" => CommunicationType.Rj45,
        "RS232" => CommunicationType.Rs232,
        "RS485" => CommunicationType.Rs485,
        _ => CommunicationType.Usb
    };
}
