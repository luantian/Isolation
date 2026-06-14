using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 实时监视视图模型
/// </summary>
public sealed partial class RealtimeMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Random _rnd = new Random(42);
    private const int MaxPoints = 300;
    private int _tickCount;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<double> _pressurePoints = [];

    [ObservableProperty]
    private ObservableCollection<double> _flowPoints = [];

    [ObservableProperty]
    private ObservableCollection<double> _tempPoints = [];

    public RealtimeMonitorViewModel()
    {
        PressurePoints = GenerateInitPoints(i => 3.0 + 0.15 * Math.Sin(i * 0.1) + 0.08 * Math.Sin(i * 0.03), 0.04);
        FlowPoints = GenerateInitPoints(i => 0.012 + 0.004 * Math.Sin(i * 0.07) + 0.003 * Math.Sin(i * 0.02), 0.001);
        TempPoints = GenerateInitPoints(i => 24.5 + 0.6 * Math.Sin(i * 0.05) + 0.3 * Math.Sin(i * 0.015), 0.15);

        _timer = new System.Timers.Timer(500) { AutoReset = true, Enabled = true };
        _timer.Elapsed += (_, _) =>
        {
            // Timer 回调在后台线程，必须调度到 UI 线程再修改 ObservableCollection
            if (!_disposed && Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!_disposed) Tick();
                });
            }
        };
    }

    private ObservableCollection<double> GenerateInitPoints(Func<int, double> wave, double noise)
    {
        var list = new ObservableCollection<double>();
        for (int i = 0; i < MaxPoints; i++)
            list.Add(wave(i) + (_rnd.NextDouble() - 0.5) * noise);
        return list;
    }

    private void Tick()
    {
        _tickCount++;
        double t = _tickCount;

        double p = 3.0 + 0.15 * Math.Sin(t * 0.1) + 0.08 * Math.Sin(t * 0.03) + (_rnd.NextDouble() - 0.5) * 0.04;
        PressurePoints.Add(Math.Clamp(p, 2.5, 3.5));
        if (PressurePoints.Count > MaxPoints) PressurePoints.RemoveAt(0);

        double f = 0.012 + 0.004 * Math.Sin(t * 0.07) + 0.003 * Math.Sin(t * 0.02) + (_rnd.NextDouble() - 0.5) * 0.001;
        FlowPoints.Add(Math.Clamp(f, 0.002, 0.030));
        if (FlowPoints.Count > MaxPoints) FlowPoints.RemoveAt(0);

        double tp = 24.5 + 0.6 * Math.Sin(t * 0.05) + 0.3 * Math.Sin(t * 0.015) + (_rnd.NextDouble() - 0.5) * 0.15;
        TempPoints.Add(Math.Clamp(tp, 23.0, 26.0));
        if (TempPoints.Count > MaxPoints) TempPoints.RemoveAt(0);

        // 同步更新 Variables 当前值
        if (Variables.Count >= 1)
        {
            Variables[0].CurrentValue = PressurePoints.Last().ToString("F3");
            Variables[0].UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        if (Variables.Count >= 2)
        {
            Variables[1].CurrentValue = FlowPoints.Last().ToString("F4");
            Variables[1].UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        if (Variables.Count >= 5)
        {
            Variables[4].CurrentValue = TempPoints.Last().ToString("F1");
            Variables[4].UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    public ObservableCollection<RealtimeVariableItem> Variables { get; } =
    [
        new()
        {
            VariableCode = "PLC_PRESSURE_MAIN",
            VariableName = "主压力",
            CurrentValue = "0.862",
            Unit = "MPa",
            Channel = "待确认：PLC DB1.DBD0",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "正常"
        },
        new()
        {
            VariableCode = "PLC_LEAK_RATE",
            VariableName = "泄漏率",
            CurrentValue = "0.014",
            Unit = "L/min",
            Channel = "待确认：PLC DB1.DBD4",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "正常"
        },
        new()
        {
            VariableCode = "PLC_TEST_STATE",
            VariableName = "试验状态",
            CurrentValue = "稳压",
            Unit = string.Empty,
            Channel = "待确认：PLC DB1.DBW8",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "正常"
        },
        new()
        {
            VariableCode = "PLC_ALARM_CODE",
            VariableName = "报警码",
            CurrentValue = "0",
            Unit = string.Empty,
            Channel = "待确认：PLC DB1.DBW10",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "无报警"
        },
        new()
        {
            VariableCode = "PLC_TEMP_ENV",
            VariableName = "环境温度",
            CurrentValue = "24.6",
            Unit = "℃",
            Channel = "待确认：PLC DB1.DBD12",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "正常"
        }
    ];

    public string ConnectionState => "待接入 PLC 通讯 DLL";

    public string ReadMode => "只读实时变量";

    public string BoundaryNote => "通过 DLL 读取 PLC 实时变量并显示当前值；不在本软件中下发试验任务或执行现场控制。";

    public double PressureMin => 2.5;
    public double PressureMax => 3.5;
    public double FlowMin => 0.002;
    public double FlowMax => 0.030;
    public double TempMin => 23.0;
    public double TempMax => 26.0;

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
