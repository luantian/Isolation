namespace IsolationLeakage.App.Models;

public sealed class RealtimeVariableItem
{
    public string VariableCode { get; set; } = string.Empty;

    public string VariableName { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// 寄存器地址描述（显示用，如 "Reg 0 (double)"）
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 关联的曲线通道：Pressure、Flow、Temp 或 null
    /// </summary>
    public string? CurveChannel { get; set; }

    /// <summary>
    /// 曲线显示最小值
    /// </summary>
    public double MinDisplay { get; set; }

    /// <summary>
    /// 曲线显示最大值
    /// </summary>
    public double MaxDisplay { get; set; }
}
