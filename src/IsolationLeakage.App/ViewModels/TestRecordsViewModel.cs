using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.ViewModels;

public sealed class TestRecordsViewModel : INotifyPropertyChanged
{
    private TestRecordItem? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "\u5168\u90e8";

    public TestRecordsViewModel()
    {
        AllRecords =
        [
            new() { RecordCode = "TR-20260526-001", ProjectName = "\u6d77\u5357\u9879\u76ee", UnitName = "\u6d77\u5357 3 \u53f7\u673a\u7ec4", ObjectCode = "1RHR040VP", ObjectName = "\u9694\u79bb\u9600", ObjectType = "\u9600\u95e8", DeviceCode = "DEV-001", DataPackageName = "PKG_1RHR040VP_20260526_1218.dat", TestTime = "2026-05-26 12:18", ImportTime = "2026-05-26 12:24", Operator = "admin", TestPressure = 0.9m, LeakageLimit = 0.05m, FinalLeakageRate = 0.012m, Result = "\u5408\u683c", Remark = "\u793a\u4f8b\u8bb0\u5f55", StepSummary = "\u5efa\u538b -> \u7a33\u538b -> \u91c7\u96c6 -> \u5224\u5b9a -> U \u76d8\u62f7\u8d1d -> \u7ed3\u679c\u5bfc\u5165", ResultFieldSummary = "\u793a\u4f8b\uff1a\u8bd5\u9a8c\u538b\u529b\u3001\u6cc4\u6f0f\u9650\u503c\u3001\u6700\u7ec8\u6cc4\u6f0f\u7387\u3001\u5224\u5b9a\u7ed3\u679c\u3001\u8bd5\u9a8c\u65f6\u95f4", ProcessChannelSummary = "\u793a\u4f8b\uff1aCSV \u8fc7\u7a0b\u91c7\u96c6\u6570\u636e\uff0c15 \u4e2a\u901a\u9053\uff0c\u6309\u65f6\u95f4\u8f74\u56de\u653e" },
            new() { RecordCode = "TR-20260526-002", ProjectName = "\u6d77\u5357\u9879\u76ee", UnitName = "\u6d77\u5357 3 \u53f7\u673a\u7ec4", ObjectCode = "1RHR041VP", ObjectName = "\u9694\u79bb\u9600", ObjectType = "\u9600\u95e8", DeviceCode = "DEV-002", DataPackageName = "PKG_1RHR041VP_20260526_1142.dat", TestTime = "2026-05-26 11:42", ImportTime = "2026-05-26 11:50", Operator = "admin", TestPressure = 0.9m, LeakageLimit = 0.05m, FinalLeakageRate = 0.018m, Result = "\u5408\u683c", Remark = "\u793a\u4f8b\u8bb0\u5f55", StepSummary = "\u5efa\u538b -> \u7a33\u538b -> \u91c7\u96c6 -> \u5224\u5b9a -> U \u76d8\u62f7\u8d1d -> \u7ed3\u679c\u5bfc\u5165", ResultFieldSummary = "\u793a\u4f8b\uff1a\u8bd5\u9a8c\u538b\u529b\u3001\u6cc4\u6f0f\u9650\u503c\u3001\u6700\u7ec8\u6cc4\u6f0f\u7387\u3001\u5224\u5b9a\u7ed3\u679c\u3001\u8bd5\u9a8c\u65f6\u95f4", ProcessChannelSummary = "\u793a\u4f8b\uff1aCSV \u8fc7\u7a0b\u91c7\u96c6\u6570\u636e\uff0c15 \u4e2a\u901a\u9053\uff0c\u6309\u65f6\u95f4\u8f74\u56de\u653e" },
            new() { RecordCode = "TR-20260526-003", ProjectName = "\u6d77\u5357\u9879\u76ee", UnitName = "\u6d77\u5357 3 \u53f7\u673a\u7ec4", ObjectCode = "RHR-SEAL-01", ObjectName = "\u5bc6\u5c01\u6027\u90e8\u4ef6", ObjectType = "\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6", DeviceCode = "DEV-003", DataPackageName = "PKG_RHR_SEAL_20260526_1016.dat", TestTime = "2026-05-26 10:16", ImportTime = "2026-05-26 10:23", Operator = "admin", TestPressure = 0.8m, LeakageLimit = 0.06m, FinalLeakageRate = 0.083m, Result = "\u4e0d\u5408\u683c", Remark = "\u793a\u4f8b\u4e0d\u5408\u683c\u8bb0\u5f55", StepSummary = "\u5efa\u538b -> \u7a33\u538b -> \u91c7\u96c6 -> \u5224\u5b9a -> U \u76d8\u62f7\u8d1d -> \u7ed3\u679c\u5bfc\u5165", ResultFieldSummary = "\u793a\u4f8b\uff1a\u8bd5\u9a8c\u538b\u529b\u3001\u6cc4\u6f0f\u9650\u503c\u3001\u6700\u7ec8\u6cc4\u6f0f\u7387\u3001\u5224\u5b9a\u7ed3\u679c\u3001\u8bd5\u9a8c\u65f6\u95f4", ProcessChannelSummary = "\u793a\u4f8b\uff1aCSV \u8fc7\u7a0b\u91c7\u96c6\u6570\u636e\uff0c15 \u4e2a\u901a\u9053\uff0c\u6309\u65f6\u95f4\u8f74\u56de\u653e" }
        ];

        FilteredRecords = new ObservableCollection<TestRecordItem>();
        ApplyQuery();
        SelectedRecord = FilteredRecords.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TestRecordItem> AllRecords { get; }

    public ObservableCollection<TestRecordItem> FilteredRecords { get; }

    public IReadOnlyList<string> ResultOptions { get; } = ["\u5168\u90e8", "\u5408\u683c", "\u4e0d\u5408\u683c"];

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public string ResultFilter
    {
        get => _resultFilter;
        set
        {
            if (SetField(ref _resultFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    public TestRecordItem? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (_selectedRecord == value)
            {
                return;
            }

            _selectedRecord = value;
            OnPropertyChanged();
            NotifySelectedRecordChanged();
        }
    }

    public string SelectedRecordTitle => SelectedRecord is null
        ? "\u672a\u9009\u62e9\u8bd5\u9a8c\u8bb0\u5f55"
        : $"{SelectedRecord.RecordCode} / {SelectedRecord.ObjectCode}";

    public string PlaybackTitle => SelectedRecord is null
        ? "\u8fc7\u7a0b\u56de\u653e"
        : $"{SelectedRecord.ObjectCode} \u8fc7\u7a0b\u56de\u653e";

    public string PressureCurveSummary => SelectedRecord is null
        ? "-"
        : $"\u8bd5\u9a8c\u538b\u529b {SelectedRecord.TestPressure:0.###} MPa\uff0c\u8fc7\u7a0b\u6570\u636e\u6309\u6570\u636e\u5305\u65f6\u5e8f\u56de\u653e\u3002";

    public string FlowCurveSummary => SelectedRecord is null
        ? "-"
        : $"\u6700\u7ec8\u6cc4\u6f0f\u7387 {SelectedRecord.FinalLeakageRate:0.###} L/min\uff0c\u9650\u503c {SelectedRecord.LeakageLimit:0.###} L/min\u3002";

    public void ApplyQuery()
    {
        var keyword = SearchText.Trim();
        var query = AllRecords.AsEnumerable();

        if (ResultFilter != "\u5168\u90e8")
        {
            query = query.Where(record => record.Result == ResultFilter);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(record =>
                Contains(record.RecordCode, keyword) ||
                Contains(record.ObjectCode, keyword) ||
                Contains(record.ObjectName, keyword) ||
                Contains(record.DeviceCode, keyword) ||
                Contains(record.DataPackageName, keyword));
        }

        var previousCode = SelectedRecord?.RecordCode;
        FilteredRecords.Clear();
        foreach (var record in query)
        {
            FilteredRecords.Add(record);
        }

        SelectedRecord = FilteredRecords.FirstOrDefault(record => record.RecordCode == previousCode) ?? FilteredRecords.FirstOrDefault();
    }

    private void NotifySelectedRecordChanged()
    {
        OnPropertyChanged(nameof(SelectedRecordTitle));
        OnPropertyChanged(nameof(PlaybackTitle));
        OnPropertyChanged(nameof(PressureCurveSummary));
        OnPropertyChanged(nameof(FlowCurveSummary));
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
