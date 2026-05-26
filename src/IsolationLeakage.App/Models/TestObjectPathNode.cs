using System.Collections.ObjectModel;

namespace IsolationLeakage.App.Models;

public sealed class TestObjectPathNode
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public PathNodeType NodeType { get; init; }

    public string? ValveType { get; init; }

    public string? ComponentType { get; init; }

    public decimal? LeakageLimit { get; init; }

    public decimal? TestPressure { get; init; }

    public string Remark { get; init; } = string.Empty;

    public int TotalTests { get; init; }

    public int FailedTests { get; init; }

    public string LatestTestTime { get; init; } = "-";

    public string LatestLeakageRate { get; init; } = "-";

    public string LatestResult { get; init; } = "-";

    public string LatestDevice { get; init; } = "-";

    public ObservableCollection<TestObjectPathNode> Children { get; } = [];

    public string DisplayName => $"{Code}  {Name}";
}
