using System.Collections.ObjectModel;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.ViewModels;

public sealed class OverviewViewModel
{
    public OverviewViewModel()
    {
        PreviewRecords =
        [
            new("1RHR040VP", "海南 3 号", "0.012 L/min", "合格", "2026-05-26 12:18"),
            new("1RHR041VP", "海南 3 号", "0.018 L/min", "合格", "2026-05-26 11:42"),
            new("1RHR042VP", "海南 3 号", "0.083 L/min", "不合格", "2026-05-26 10:16"),
            new("1RCV010VP", "海南 3 号", "0.009 L/min", "合格", "2026-05-25 16:05"),
        ];
    }

    public string TestObjectTitle => "试验对象";
    public string TestObjectValue => "126";
    public string TestObjectUnit => "个";
    public string TestObjectDesc => "阀门 / 贯穿件 / 部件";
    public string TestObjectStatus => "台账";

    public string DeviceTitle => "测量装置";
    public string DeviceValue => "4";
    public string DeviceUnit => "台";
    public string DeviceDesc => "3 台在线，1 台离线";
    public string DeviceStatus => "装置";

    public string RecordTitle => "历史记录";
    public string RecordValue => "1850";
    public string RecordUnit => "条";
    public string RecordDesc => "按时间顺序保存";
    public string RecordStatus => "记录";

    public string PassRateTitle => "本月合格率";
    public string PassRateValue => "96.8";
    public string PassRateUnit => "%";
    public string PassRateDesc => "按导入记录统计";
    public string PassRateStatus => "统计";

    public string AnomalyTitle => "待处理异常";
    public string AnomalyValue => "3";
    public string AnomalyUnit => "项";
    public string AnomalyDesc => "不合格 / 导入异常";
    public string AnomalyStatus => "异常";

    public string BackupTitle => "最近备份";
    public string BackupValue => "02:00";
    public string BackupUnit => "";
    public string BackupDesc => "2026-05-26 自动备份";
    public string BackupStatus => "完整";

    public ObservableCollection<PreviewRecord> PreviewRecords { get; }
}

public sealed record PreviewRecord(string ObjectCode, string Unit, string LeakageRate, string Result, string UploadedAt);
