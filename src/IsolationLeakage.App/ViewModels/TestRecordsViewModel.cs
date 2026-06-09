using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.ViewModels;

public sealed class TestRecordsViewModel : INotifyPropertyChanged
{
    private TestRecordItem? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "全部";

    // 图表绑定属性（跟随 SelectedRecord 变化）
    public ObservableCollection<double> PressureCurvePoints { get; } = [];
    public ObservableCollection<double> FlowCurvePoints { get; } = [];
    public ObservableCollection<double> TempCurvePoints { get; } = [];

    public double PressureCurveMin { get; private set; }
    public double PressureCurveMax { get; private set; }
    public double FlowCurveMin { get; private set; }
    public double FlowCurveMax { get; private set; }
    public double TempCurveMin { get; private set; }
    public double TempCurveMax { get; private set; }

    public TestRecordsViewModel()
    {
        AllRecords =
        [
            CreateRecord("TR-20260526-001", "海南项目", "海南 3 号机组", "1RHR040VP", "隔离阀", "阀门", "DEV-001", "PKG_1RHR040VP_20260526_1218.dat", "2026-05-26 12:18", "2026-05-26 12:24", "admin", 0.9m, 0.05m, 0.012m, "合格"),
            CreateRecord("TR-20260526-002", "海南项目", "海南 3 号机组", "1RHR041VP", "隔离阀", "阀门", "DEV-002", "PKG_1RHR041VP_20260526_1142.dat", "2026-05-26 11:42", "2026-05-26 11:50", "admin", 0.9m, 0.05m, 0.018m, "合格"),
            CreateRecord("TR-20260526-003", "海南项目", "海南 3 号机组", "RHR-SEAL-01", "密封性部件", "其他密封性部件", "DEV-003", "PKG_RHR_SEAL_20260526_1016.dat", "2026-05-26 10:16", "2026-05-26 10:23", "admin", 0.8m, 0.06m, 0.083m, "不合格"),
        ];

        FilteredRecords = new ObservableCollection<TestRecordItem>();
        ApplyQuery();
        SelectedRecord = FilteredRecords.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TestRecordItem> AllRecords { get; }

    public ObservableCollection<TestRecordItem> FilteredRecords { get; }

    public IReadOnlyList<string> ResultOptions { get; } = ["全部", "合格", "不合格"];

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
            if (_selectedRecord == value) return;
            _selectedRecord = value;
            OnPropertyChanged();
            NotifySelectedRecordChanged();
            UpdateChartFromSelected();
        }
    }

    private static TestRecordItem CreateRecord(
        string code, string project, string unit, string objCode, string objName, string objType,
        string device, string pkg, string testTime, string importTime, string op,
        decimal pressure, decimal limit, decimal rate, string result)
    {
        var rnd = new Random(code.GetHashCode());
        const int n = 200;

        // 模拟试验过程曲线：建压 -> 稳压 -> 采集
        var pressureData = new ObservableCollection<double>();
        var flowData = new ObservableCollection<double>();
        var tempData = new ObservableCollection<double>();

        double basePressure = (double)pressure;
        double baseFlow = (double)rate;
        double baseTemp = 24.0 + rnd.NextDouble() * 1.5;

        double pMin = double.MaxValue, pMax = double.MinValue;
        double fMin = double.MaxValue, fMax = double.MinValue;
        double tMin = double.MaxValue, tMax = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)n;
            double p, f, tp;

            if (t < 0.15)
            {
                // 建压阶段：压力从 0 快速上升
                double phase = t / 0.15;
                p = basePressure * (1 - Math.Exp(-phase * 4)) + rnd.NextDouble() * 0.02;
                f = baseFlow * (2 + rnd.NextDouble()) * (1 - phase) + rnd.NextDouble() * 0.005;
                tp = baseTemp - 0.3 + rnd.NextDouble() * 0.2;
            }
            else if (t < 0.3)
            {
                // 稳压阶段：压力微降后稳定
                double phase = (t - 0.15) / 0.15;
                p = basePressure * (1.05 - 0.05 * phase) + (rnd.NextDouble() - 0.5) * 0.01;
                f = baseFlow * (1.5 + 0.5 * Math.Sin(phase * 10) * (1 - phase)) + (rnd.NextDouble() - 0.5) * 0.003;
                tp = baseTemp + 0.2 * phase + (rnd.NextDouble() - 0.5) * 0.15;
            }
            else
            {
                // 采集阶段：压力稳定，泄漏率波动
                double phase = (t - 0.3) / 0.7;
                p = basePressure + (rnd.NextDouble() - 0.5) * 0.008 - phase * 0.01;
                f = baseFlow + 0.003 * Math.Sin(phase * 20) + (rnd.NextDouble() - 0.5) * 0.002;
                tp = baseTemp + 0.3 + 0.1 * Math.Sin(phase * 5) + (rnd.NextDouble() - 0.5) * 0.1;
            }

            p = Math.Max(0, p);
            f = Math.Max(0, f);

            pressureData.Add(p);
            flowData.Add(f);
            tempData.Add(tp);

            pMin = Math.Min(pMin, p); pMax = Math.Max(pMax, p);
            fMin = Math.Min(fMin, f); fMax = Math.Max(fMax, f);
            tMin = Math.Min(tMin, tp); tMax = Math.Max(tMax, tp);
        }

        // 扩宽范围边界 5%
        double expand(double v, double range, bool up) => up ? v + range * 0.05 : v - range * 0.05;
        double pRange = pMax - pMin; if (pRange == 0) pRange = 0.1;
        double fRange = fMax - fMin; if (fRange == 0) fRange = 0.001;
        double tRange = tMax - tMin; if (tRange == 0) tRange = 0.5;

        return new TestRecordItem
        {
            RecordCode = code,
            ProjectName = project,
            UnitName = unit,
            ObjectCode = objCode,
            ObjectName = objName,
            ObjectType = objType,
            DeviceCode = device,
            DataPackageName = pkg,
            TestTime = testTime,
            ImportTime = importTime,
            Operator = op,
            TestPressure = pressure,
            LeakageLimit = limit,
            FinalLeakageRate = rate,
            Result = result,
            Remark = "示例记录",
            StepSummary = "建压 -> 稳压 -> 采集 -> 判定 -> U 盘拷贝 -> 结果导入",
            ResultFieldSummary = "示例：试验压力、泄漏限值、最终泄漏率、判定结果、试验时间",
            ProcessChannelSummary = "示例：CSV 过程采集数据，15 个通道，按时间轴回放",
            PressureCurveData = pressureData,
            FlowCurveData = flowData,
            TempCurveData = tempData,
            PressureMin = expand(pMin, pRange, false),
            PressureMax = expand(pMax, pRange, true),
            FlowMin = expand(fMin, fRange, false),
            FlowMax = expand(fMax, fRange, true),
            TempMin = expand(tMin, tRange, false),
            TempMax = expand(tMax, tRange, true),
        };
    }

    public string SelectedRecordTitle => SelectedRecord is null
        ? "未选择试验记录"
        : $"{SelectedRecord.RecordCode} / {SelectedRecord.ObjectCode}";

    public string PlaybackTitle => SelectedRecord is null
        ? "过程回放"
        : $"{SelectedRecord.ObjectCode} 过程回放";

    public string PressureCurveSummary => SelectedRecord is null
        ? "-"
        : $"试验压力 {SelectedRecord.TestPressure:0.###} MPa，过程数据按数据包时序回放。";

    public string FlowCurveSummary => SelectedRecord is null
        ? "-"
        : $"最终泄漏率 {SelectedRecord.FinalLeakageRate:0.###} L/min，限值 {SelectedRecord.LeakageLimit:0.###} L/min。";

    public void ApplyQuery()
    {
        var keyword = SearchText.Trim();
        var query = AllRecords.AsEnumerable();

        if (ResultFilter != "全部")
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

    private void UpdateChartFromSelected()
    {
        PressureCurvePoints.Clear();
        FlowCurvePoints.Clear();
        TempCurvePoints.Clear();

        if (SelectedRecord?.PressureCurveData != null)
        {
            foreach (var v in SelectedRecord.PressureCurveData) PressureCurvePoints.Add(v);
            foreach (var v in SelectedRecord.FlowCurveData!) FlowCurvePoints.Add(v);
            foreach (var v in SelectedRecord.TempCurveData!) TempCurvePoints.Add(v);

            PressureCurveMin = SelectedRecord.PressureMin;
            PressureCurveMax = SelectedRecord.PressureMax;
            FlowCurveMin = SelectedRecord.FlowMin;
            FlowCurveMax = SelectedRecord.FlowMax;
            TempCurveMin = SelectedRecord.TempMin;
            TempCurveMax = SelectedRecord.TempMax;
        }

        OnPropertyChanged(nameof(PressureCurvePoints));
        OnPropertyChanged(nameof(FlowCurvePoints));
        OnPropertyChanged(nameof(TempCurvePoints));
        OnPropertyChanged(nameof(PressureCurveMin));
        OnPropertyChanged(nameof(PressureCurveMax));
        OnPropertyChanged(nameof(FlowCurveMin));
        OnPropertyChanged(nameof(FlowCurveMax));
        OnPropertyChanged(nameof(TempCurveMin));
        OnPropertyChanged(nameof(TempCurveMax));
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
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
