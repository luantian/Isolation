using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验记录视图模型（简化版 - 只负责记录查询和详情展示）
/// </summary>
public sealed partial class TestRecordsViewModel : ViewModelBase
{
    private TestRecord? _selectedRecord;
    private string _searchText = string.Empty;
    private string _resultFilter = "全部";
    private bool _isLoading;
    private string _statusMessage = "加载中...";
    private int _totalCount;

    // 曲线数据
    public ObservableCollection<double> PressureCurvePoints { get; } = [];
    public ObservableCollection<double> FlowCurvePoints { get; } = [];
    public ObservableCollection<double> TempCurvePoints { get; } = [];

    // 曲线范围
    private double _pressureMin;
    private double _pressureMax;
    private double _flowMin;
    private double _flowMax;
    private double _tempMin;
    private double _tempMax;

    public TestRecordsViewModel()
    {
        ResultOptions = ["全部", "合格", "不合格"];
        FilteredRecords = [];
        _ = LoadDataAsync();
    }

    public ObservableCollection<TestRecord> FilteredRecords { get; }

    public IReadOnlyList<string> ResultOptions { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string ResultFilter
    {
        get => _resultFilter;
        set
        {
            if (SetProperty(ref _resultFilter, value))
            {
                ApplyQuery();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    public TestRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                UpdateChartFromSelected();
            }
        }
    }

    // 曲线范围属性
    public double PressureMin
    {
        get => _pressureMin;
        set => SetProperty(ref _pressureMin, value);
    }

    public double PressureMax
    {
        get => _pressureMax;
        set => SetProperty(ref _pressureMax, value);
    }

    public double FlowMin
    {
        get => _flowMin;
        set => SetProperty(ref _flowMin, value);
    }

    public double FlowMax
    {
        get => _flowMax;
        set => SetProperty(ref _flowMax, value);
    }

    public double TempMin
    {
        get => _tempMin;
        set => SetProperty(ref _tempMin, value);
    }

    public double TempMax
    {
        get => _tempMax;
        set => SetProperty(ref _tempMax, value);
    }

    public ICommand QueryCommand => new RelayCommand(ApplyQuery);

    /// <summary>
    /// 初始加载数据
    /// </summary>
    private async Task LoadDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在加载...";

            using var context = DbContextFactory.CreateDbContext();

            var query = BuildBaseQuery(context);
            var records = await query.Take(100).ToListAsync();
            TotalCount = await context.TestRecords.CountAsync();

            FilteredRecords.Clear();
            foreach (var record in records)
            {
                FilteredRecords.Add(record);
            }

            SelectedRecord = FilteredRecords.FirstOrDefault();
            StatusMessage = $"共 {TotalCount:N0} 条记录";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 应用查询过滤
    /// </summary>
    public async void ApplyQuery()
    {
        try
        {
            IsLoading = true;

            using var context = DbContextFactory.CreateDbContext();
            var query = BuildBaseQuery(context);

            // 结果过滤
            if (ResultFilter != "全部")
            {
                var targetResult = ResultFilter == "合格" ? TestResult.Pass : TestResult.Fail;
                query = query.Where(r => r.Result == targetResult);
            }

            // 关键字搜索
            var keyword = SearchText.Trim();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(r =>
                    EF.Functions.Like(r.RecordCode, $"%{keyword}%") ||
                    EF.Functions.Like(r.ObjectCode, $"%{keyword}%") ||
                    EF.Functions.Like(r.ObjectName, $"%{keyword}%") ||
                    EF.Functions.Like(r.DeviceCode, $"%{keyword}%") ||
                    EF.Functions.Like(r.DataPackageName, $"%{keyword}%"));
            }

            var records = await query
                .OrderByDescending(r => r.TestTime)
                .Take(100)
                .ToListAsync();

            var previousCode = SelectedRecord?.RecordCode;
            FilteredRecords.Clear();
            foreach (var record in records)
            {
                FilteredRecords.Add(record);
            }

            SelectedRecord = FilteredRecords.FirstOrDefault(r => r.RecordCode == previousCode)
                           ?? FilteredRecords.FirstOrDefault();

            StatusMessage = $"查询结果 {FilteredRecords.Count:N0} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 更新选中记录的曲线数据
    /// </summary>
    private async void UpdateChartFromSelected()
    {
        PressureCurvePoints.Clear();
        FlowCurvePoints.Clear();
        TempCurvePoints.Clear();

        if (SelectedRecord == null)
        {
            // 设置默认范围
            PressureMin = 0;
            PressureMax = 1;
            FlowMin = 0;
            FlowMax = 0.01;
            TempMin = 20;
            TempMax = 30;
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var processData = await context.TestProcessData
                .FirstOrDefaultAsync(d => d.RecordCode == SelectedRecord.RecordCode);

            if (processData != null)
            {
                var pressureData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.PressureCurveJson ?? "[]") ?? [];
                var flowData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.FlowCurveJson ?? "[]") ?? [];
                var tempData = System.Text.Json.JsonSerializer.Deserialize<double[]>(processData.TempCurveJson ?? "[]") ?? [];

                foreach (var p in pressureData) PressureCurvePoints.Add(p);
                foreach (var f in flowData) FlowCurvePoints.Add(f);
                foreach (var t in tempData) TempCurvePoints.Add(t);

                // 使用数据库中存储的范围
                PressureMin = (double)processData.PressureMin;
                PressureMax = (double)processData.PressureMax;
                FlowMin = (double)processData.FlowMin;
                FlowMax = (double)processData.FlowMax;
                TempMin = (double)processData.TempMin;
                TempMax = (double)processData.TempMax;

                return;
            }
        }
        catch
        {
            // 读取失败时生成模拟数据
        }

        // 生成模拟曲线数据（没有真实数据时，用于展示）
        GenerateSampleCurve();
    }

    /// <summary>
    /// 生成模拟曲线数据（用于展示效果）
    /// </summary>
    private void GenerateSampleCurve()
    {
        var rnd = new Random(SelectedRecord?.RecordCode?.GetHashCode() ?? 42);
        const int n = 200;
        double basePressure = (double)(SelectedRecord?.TestPressure ?? 0.9m);
        double baseFlow = (double)(SelectedRecord?.FinalLeakageRate ?? 0.012m);
        double baseTemp = 24.5;

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)n;
            double p, f, tp;

            if (t < 0.15)
            {
                double phase = t / 0.15;
                p = basePressure * (1 - Math.Exp(-phase * 4)) + rnd.NextDouble() * 0.02;
                f = baseFlow * (2 + rnd.NextDouble()) * (1 - phase) + rnd.NextDouble() * 0.001;
                tp = baseTemp - 0.3 + rnd.NextDouble() * 0.2;
            }
            else if (t < 0.3)
            {
                double phase = (t - 0.15) / 0.15;
                p = basePressure * (1.05 - 0.05 * phase) + (rnd.NextDouble() - 0.5) * 0.01;
                f = baseFlow * (1.5 + 0.5 * Math.Sin(phase * 10) * (1 - phase)) + (rnd.NextDouble() - 0.5) * 0.003;
                tp = baseTemp + 0.2 * phase + (rnd.NextDouble() - 0.5) * 0.15;
            }
            else
            {
                double phase = (t - 0.3) / 0.7;
                p = basePressure + (rnd.NextDouble() - 0.5) * 0.008 - phase * 0.01;
                f = baseFlow + 0.003 * Math.Sin(phase * 20) + (rnd.NextDouble() - 0.5) * 0.002;
                tp = baseTemp + 0.3 + 0.1 * Math.Sin(phase * 5) + (rnd.NextDouble() - 0.5) * 0.1;
            }

            p = Math.Max(0, p);
            f = Math.Max(0, f);

            PressureCurvePoints.Add(p);
            FlowCurvePoints.Add(f);
            TempCurvePoints.Add(tp);
        }

        // 设置合理的范围值
        PressureMin = 0;
        PressureMax = basePressure * 1.2;
        FlowMin = 0;
        FlowMax = baseFlow * 2;
        TempMin = 23.0;
        TempMax = 26.0;
    }

    /// <summary>
    /// 构建基础查询（包含关联数据）
    /// </summary>
    private static IQueryable<TestRecord> BuildBaseQuery(AppDbContext context)
    {
        return context.TestRecords
            .Include(r => r.Project)
            .Include(r => r.Unit)
            .Include(r => r.Device)
            .AsQueryable();
    }
}
