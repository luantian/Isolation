using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

public sealed class MeasurementDeviceLedgerViewModel : INotifyPropertyChanged
{
    private readonly MasterDataStore _store;
    private MeasurementDeviceItem? _selectedDevice;
    private string _deviceCode = string.Empty;
    private string _deviceName = string.Empty;
    private string _enabledFilter = "\u5168\u90e8";
    private bool _isNewMode;
    private string _message = "\u505c\u7528\u540e\u4e0d\u518d\u4f5c\u4e3a\u65b0\u6570\u636e\u5305\u5bfc\u5165\u7684\u53ef\u9009\u88c5\u7f6e\uff0c\u5386\u53f2\u6570\u636e\u4fdd\u7559\u3002";
    private string _model = string.Empty;
    private string _primaryCommunication = "USB";
    private string _communicationFilter = "\u5168\u90e8";
    private string _remark = string.Empty;
    private string _searchText = string.Empty;
    private string _serialNumber = string.Empty;

    public MeasurementDeviceLedgerViewModel(MasterDataStore store)
    {
        _store = store;
        FilteredDevices = new ObservableCollection<MeasurementDeviceItem>();
        ApplyQuery();
        SelectedDevice = FilteredDevices.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MeasurementDeviceItem> FilteredDevices { get; }

    public IReadOnlyList<string> CommunicationOptions { get; } = ["USB", "RJ45", "RS232/485", "\u5f85\u786e\u8ba4\uff1a\u5176\u4ed6"];

    public IReadOnlyList<string> CommunicationFilterOptions { get; } = ["\u5168\u90e8", "USB", "RJ45", "RS232/485", "\u5f85\u786e\u8ba4\uff1a\u5176\u4ed6"];

    public IReadOnlyList<string> EnabledFilterOptions { get; } = ["\u5168\u90e8", "\u542f\u7528", "\u505c\u7528"];

    public MeasurementDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (_selectedDevice == value)
            {
                return;
            }

            _selectedDevice = value;
            LoadSelectedDevice();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExistingMode));
            NotifyReadOnlyStatusChanged();
        }
    }

    public string CommunicationFilter
    {
        get => _communicationFilter;
        set
        {
            if (SetField(ref _communicationFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    public string EnabledFilter
    {
        get => _enabledFilter;
        set
        {
            if (SetField(ref _enabledFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public bool IsNewMode
    {
        get => _isNewMode;
        private set
        {
            if (SetField(ref _isNewMode, value))
            {
                OnPropertyChanged(nameof(IsExistingMode));
            }
        }
    }

    public bool IsExistingMode => !IsNewMode && SelectedDevice is not null;

    public string DeviceCode
    {
        get => _deviceCode;
        set => SetField(ref _deviceCode, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetField(ref _deviceName, value);
    }

    public string Model
    {
        get => _model;
        set => SetField(ref _model, value);
    }

    public string SerialNumber
    {
        get => _serialNumber;
        set => SetField(ref _serialNumber, value);
    }

    public string PrimaryCommunication
    {
        get => _primaryCommunication;
        set => SetField(ref _primaryCommunication, value);
    }

    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    public string EnabledStatus => SelectedDevice?.EnabledStatus ?? "\u542f\u7528";

    public string RecentConnectionStatus => SelectedDevice?.RecentConnectionStatus ?? "\u672a\u540c\u6b65";

    public string LastSyncTime => SelectedDevice?.LastSyncTime ?? "-";

    public string LastUploadTime => SelectedDevice?.LastUploadTime ?? "-";

    public string UploadCountText => SelectedDevice?.UploadCount.ToString() ?? "0";

    public string LastUploadResult => SelectedDevice?.LastUploadResult ?? "-";

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public void ApplyQuery()
    {
        var keyword = SearchText.Trim();
        var query = _store.MeasurementDevices.AsEnumerable();

        if (CommunicationFilter != "\u5168\u90e8")
        {
            query = query.Where(device => device.PrimaryCommunication == CommunicationFilter);
        }

        if (EnabledFilter != "\u5168\u90e8")
        {
            query = query.Where(device => device.EnabledStatus == EnabledFilter);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(device =>
                Contains(device.DeviceCode, keyword) ||
                Contains(device.DeviceName, keyword) ||
                Contains(device.Model, keyword) ||
                Contains(device.SerialNumber, keyword));
        }

        var previousCode = SelectedDevice?.DeviceCode;
        FilteredDevices.Clear();
        foreach (var device in query)
        {
            FilteredDevices.Add(device);
        }

        SelectedDevice = FilteredDevices.FirstOrDefault(device => device.DeviceCode == previousCode) ?? FilteredDevices.FirstOrDefault();
        Message = FilteredDevices.Count == 0 ? "\u672a\u67e5\u5230\u5339\u914d\u7684\u6d4b\u91cf\u88c5\u7f6e\u3002" : $"\u5df2\u67e5\u8be2\u5230 {FilteredDevices.Count} \u53f0\u88c5\u7f6e\u3002";
    }

    public void StartNew()
    {
        IsNewMode = true;
        SelectedDevice = null;
        DeviceCode = $"DEV-{_store.MeasurementDevices.Count + 1:000}";
        DeviceName = string.Empty;
        Model = string.Empty;
        SerialNumber = string.Empty;
        PrimaryCommunication = "USB";
        Remark = string.Empty;
        NotifyReadOnlyStatusChanged();
        Message = "\u6b63\u5728\u65b0\u589e\u6d4b\u91cf\u88c5\u7f6e\uff0c\u4fdd\u5b58\u540e\u88c5\u7f6e\u7f16\u53f7\u4e0d\u5141\u8bb8\u4fee\u6539\u3002";
    }

    public void Save()
    {
        if (!ValidateEditor())
        {
            return;
        }

        if (IsNewMode)
        {
            var device = new MeasurementDeviceItem
            {
                DeviceCode = DeviceCode.Trim(),
                DeviceName = DeviceName.Trim(),
                Model = Model.Trim(),
                SerialNumber = SerialNumber.Trim(),
                PrimaryCommunication = PrimaryCommunication,
                EnabledStatus = "\u542f\u7528",
                RecentConnectionStatus = "\u672a\u540c\u6b65",
                LastSyncTime = "-",
                LastUploadTime = "-",
                UploadCount = 0,
                LastUploadResult = "-",
                Remark = Remark.Trim()
            };

            _store.MeasurementDevices.Add(device);
            IsNewMode = false;
            ApplyQuery();
            SelectedDevice = device;
            Message = "\u5df2\u65b0\u589e\u6d4b\u91cf\u88c5\u7f6e\u3002";
            return;
        }

        if (SelectedDevice is null)
        {
            Message = "\u8bf7\u5148\u9009\u62e9\u88c5\u7f6e\u3002";
            return;
        }

        SelectedDevice.DeviceName = DeviceName.Trim();
        SelectedDevice.Model = Model.Trim();
        SelectedDevice.SerialNumber = SerialNumber.Trim();
        SelectedDevice.PrimaryCommunication = PrimaryCommunication;
        SelectedDevice.Remark = Remark.Trim();
        ApplyQuery();
        Message = "\u5df2\u4fdd\u5b58\u88c5\u7f6e\u57fa\u7840\u4fe1\u606f\u3002";
    }

    public void EnableSelected()
    {
        SetSelectedEnabledStatus("\u542f\u7528");
    }

    public void DisableSelected()
    {
        SetSelectedEnabledStatus("\u505c\u7528");
    }

    private void SetSelectedEnabledStatus(string status)
    {
        if (SelectedDevice is null)
        {
            Message = "\u8bf7\u5148\u9009\u62e9\u88c5\u7f6e\u3002";
            return;
        }

        SelectedDevice.EnabledStatus = status;
        NotifyReadOnlyStatusChanged();
        ApplyQuery();
        Message = status == "\u505c\u7528"
            ? "\u5df2\u505c\u7528\u88c5\u7f6e\u3002\u505c\u7528\u540e\u4e0d\u518d\u4f5c\u4e3a\u65b0\u6570\u636e\u5305\u5bfc\u5165\u7684\u53ef\u9009\u88c5\u7f6e\uff0c\u5386\u53f2\u6570\u636e\u4fdd\u7559\u3002"
            : "\u5df2\u542f\u7528\u88c5\u7f6e\u3002";
    }

    private bool ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(DeviceCode))
        {
            Message = "\u88c5\u7f6e\u7f16\u53f7\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        if (IsNewMode && _store.MeasurementDevices.Any(device => device.DeviceCode == DeviceCode.Trim()))
        {
            Message = "\u88c5\u7f6e\u7f16\u53f7\u5df2\u5b58\u5728\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            Message = "\u88c5\u7f6e\u540d\u79f0\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PrimaryCommunication))
        {
            Message = "\u4e3b\u901a\u4fe1\u65b9\u5f0f\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        return true;
    }

    private void LoadSelectedDevice()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsNewMode = false;
        DeviceCode = SelectedDevice.DeviceCode;
        DeviceName = SelectedDevice.DeviceName;
        Model = SelectedDevice.Model;
        SerialNumber = SelectedDevice.SerialNumber;
        PrimaryCommunication = SelectedDevice.PrimaryCommunication;
        Remark = SelectedDevice.Remark;
    }

    private void NotifyReadOnlyStatusChanged()
    {
        OnPropertyChanged(nameof(EnabledStatus));
        OnPropertyChanged(nameof(RecentConnectionStatus));
        OnPropertyChanged(nameof(LastSyncTime));
        OnPropertyChanged(nameof(LastUploadTime));
        OnPropertyChanged(nameof(UploadCountText));
        OnPropertyChanged(nameof(LastUploadResult));
    }

    private static bool Contains(string source, string keyword)
    {
        return source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
