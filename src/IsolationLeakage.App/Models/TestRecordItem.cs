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
}
