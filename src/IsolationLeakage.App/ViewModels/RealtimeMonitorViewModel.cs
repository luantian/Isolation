using System.Collections.ObjectModel;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.ViewModels;

public sealed class RealtimeMonitorViewModel
{
    public ObservableCollection<RealtimeVariableItem> Variables { get; } =
    [
        new()
        {
            VariableCode = "PLC_PRESSURE_MAIN",
            VariableName = "\u4e3b\u538b\u529b",
            CurrentValue = "0.862",
            Unit = "MPa",
            Channel = "\u5f85\u786e\u8ba4\uff1aPLC DB1.DBD0",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "\u6b63\u5e38"
        },
        new()
        {
            VariableCode = "PLC_LEAK_RATE",
            VariableName = "\u6cc4\u6f0f\u7387",
            CurrentValue = "0.014",
            Unit = "L/min",
            Channel = "\u5f85\u786e\u8ba4\uff1aPLC DB1.DBD4",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "\u6b63\u5e38"
        },
        new()
        {
            VariableCode = "PLC_TEST_STATE",
            VariableName = "\u8bd5\u9a8c\u72b6\u6001",
            CurrentValue = "\u7a33\u538b",
            Unit = string.Empty,
            Channel = "\u5f85\u786e\u8ba4\uff1aPLC DB1.DBW8",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "\u6b63\u5e38"
        },
        new()
        {
            VariableCode = "PLC_ALARM_CODE",
            VariableName = "\u62a5\u8b66\u7801",
            CurrentValue = "0",
            Unit = string.Empty,
            Channel = "\u5f85\u786e\u8ba4\uff1aPLC DB1.DBW10",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "\u65e0\u62a5\u8b66"
        },
        new()
        {
            VariableCode = "PLC_TEMP_ENV",
            VariableName = "\u73af\u5883\u6e29\u5ea6",
            CurrentValue = "24.6",
            Unit = "\u2103",
            Channel = "\u5f85\u786e\u8ba4\uff1aPLC DB1.DBD12",
            UpdatedAt = "2026-05-26 14:35:12",
            Status = "\u6b63\u5e38"
        }
    ];

    public string ConnectionState => "\u5f85\u63a5\u5165 PLC \u901a\u8baf DLL";

    public string ReadMode => "\u53ea\u8bfb\u5b9e\u65f6\u53d8\u91cf";

    public string BoundaryNote => "\u901a\u8fc7 DLL \u8bfb\u53d6 PLC \u5b9e\u65f6\u53d8\u91cf\u5e76\u663e\u793a\u5f53\u524d\u503c\uff1b\u4e0d\u5728\u672c\u8f6f\u4ef6\u4e2d\u4e0b\u53d1\u8bd5\u9a8c\u4efb\u52a1\u6216\u6267\u884c\u73b0\u573a\u63a7\u5236\u3002";
}
