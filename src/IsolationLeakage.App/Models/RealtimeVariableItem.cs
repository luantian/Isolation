namespace IsolationLeakage.App.Models;

public sealed class RealtimeVariableItem
{
    public string VariableCode { get; set; } = string.Empty;

    public string VariableName { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}
