using System.Collections.ObjectModel;

namespace IsolationLeakage.App.Models;

public sealed class TestRecordItem
{
    public string RecordCode { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string UnitName { get; set; } = string.Empty;

    public string ObjectCode { get; set; } = string.Empty;

    public string ObjectName { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string DataPackageName { get; set; } = string.Empty;

    public string TestTime { get; set; } = string.Empty;

    public string ImportTime { get; set; } = string.Empty;

    public string Operator { get; set; } = string.Empty;

    public decimal TestPressure { get; set; }

    public decimal LeakageLimit { get; set; }

    public decimal FinalLeakageRate { get; set; }

    public string Result { get; set; } = string.Empty;

    public string Remark { get; set; } = string.Empty;

    public string StepSummary { get; set; } = string.Empty;

    public string ResultFieldSummary { get; set; } = string.Empty;

    public string ProcessChannelSummary { get; set; } = string.Empty;

    // 过程曲线数据（用于多通道折线图）
    public ObservableCollection<double>? PressureCurveData { get; set; }

    public ObservableCollection<double>? FlowCurveData { get; set; }

    public ObservableCollection<double>? TempCurveData { get; set; }

    public double PressureMin { get; set; }

    public double PressureMax { get; set; }

    public double FlowMin { get; set; }

    public double FlowMax { get; set; }

    public double TempMin { get; set; }

    public double TempMax { get; set; }
}
