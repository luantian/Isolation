using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

public sealed class TestObjectPathManagementViewModel : INotifyPropertyChanged
{
    private readonly MasterDataStore _store;
    private int _componentSequence;
    private string _locateMessage = "\u8f93\u5165\u7f16\u53f7\u6216\u540d\u79f0\u540e\u70b9\u51fb\u5b9a\u4f4d\u3002";
    private int _penetrationSequence;
    private string _searchText = string.Empty;
    private string _selectedProject = string.Empty;
    private string _selectedUnit = string.Empty;
    private int _systemSequence;
    private int _valveSequence;
    private TestObjectPathNode? _selectedNode;

    public TestObjectPathManagementViewModel(MasterDataStore store)
    {
        _store = store;
        RefreshProjects();
        LoadPathTree();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Projects { get; } = [];

    public ObservableCollection<string> Units { get; } = [];

    public ObservableCollection<TestObjectPathNode> PathTree { get; } = [];

    public string SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetField(ref _selectedProject, value))
            {
                return;
            }

            RefreshUnits();
            LoadPathTree();
            OnPropertyChanged(nameof(CurrentScopeText));
        }
    }

    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!SetField(ref _selectedUnit, value))
            {
                return;
            }

            LoadPathTree();
            OnPropertyChanged(nameof(CurrentScopeText));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public string LocateMessage
    {
        get => _locateMessage;
        set => SetField(ref _locateMessage, value);
    }

    public TestObjectPathNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value)
            {
                return;
            }

            _selectedNode = value;
            NotifySelectionChanged();
        }
    }

    public string CurrentScopeText => string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(SelectedUnit)
        ? "\u5f53\u524d\u8303\u56f4\uff1a\u672a\u9009\u62e9\u9879\u76ee/\u673a\u7ec4"
        : $"\u5f53\u524d\u8303\u56f4\uff1a{SelectedProject} / {SelectedUnit}";

    public string DetailTitle => SelectedNode is null ? "\u672a\u9009\u62e9\u8def\u5f84" : $"{NodeTypeText}\u8be6\u60c5";

    public string AvailableCreateText => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "\u8d2f\u7a7f\u4ef6\u3001\u9600\u95e8\u3001\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6",
        PathNodeType.Penetration => "\u9600\u95e8\u3001\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6",
        PathNodeType.Valve => "\u65e0",
        PathNodeType.OtherComponent => "\u65e0",
        _ => "\u7cfb\u7edf\u3001\u8d2f\u7a7f\u4ef6\u3001\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6"
    };

    public string NodeOperationDescription => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "\u7cfb\u7edf\u7528\u4e8e\u5f52\u96c6\u8be5\u7cfb\u7edf\u4e0b\u7684\u8bd5\u9a8c\u5bf9\u8c61\u8def\u5f84\u3002",
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6\u4e0b\u53ef\u6302\u63a5\u9600\u95e8\u6216\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6\u3002",
        PathNodeType.Valve => "\u9600\u95e8\u662f\u8bd5\u9a8c\u5bf9\u8c61\u672b\u7ea7\u8282\u70b9\uff0c\u4e0d\u518d\u7ee7\u7eed\u6302\u63a5\u5b50\u8282\u70b9\u3002",
        PathNodeType.OtherComponent => "\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6\u662f\u8bd5\u9a8c\u5bf9\u8c61\u672b\u7ea7\u8282\u70b9\uff0c\u4e0d\u518d\u7ee7\u7eed\u6302\u63a5\u5b50\u8282\u70b9\u3002",
        _ => "\u8bf7\u5148\u9009\u62e9\u8def\u5f84\u8282\u70b9\uff1b\u9600\u95e8\u9ed8\u8ba4\u4e0d\u76f4\u63a5\u6302\u5728\u673a\u7ec4\u4e0b\u3002"
    };

    public string NodeTypeText => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "\u5de5\u827a\u7cfb\u7edf",
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6",
        PathNodeType.Valve => "\u9600\u95e8",
        PathNodeType.OtherComponent => "\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6",
        _ => "-"
    };

    public string LimitLabel => SelectedNode?.NodeType switch
    {
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6\u6cc4\u6f0f\u7387\u9650\u503c",
        PathNodeType.Valve => "\u9600\u95e8\u6cc4\u6f0f\u7387\u9650\u503c",
        PathNodeType.OtherComponent => "\u90e8\u4ef6\u6cc4\u6f0f\u7387\u9650\u503c",
        _ => "\u6cc4\u6f0f\u7387\u9650\u503c"
    };

    public string PressureLabel => SelectedNode?.NodeType == PathNodeType.Valve ? "\u9600\u95e8\u8bd5\u9a8c\u538b\u529b" : "\u8bd5\u9a8c\u538b\u529b";

    public string TypeLabel => SelectedNode?.NodeType == PathNodeType.Valve ? "\u9600\u95e8\u7c7b\u578b" : "\u90e8\u4ef6\u7c7b\u578b";

    public string TypeValue => SelectedNode?.NodeType switch
    {
        PathNodeType.Valve => SelectedNode.ValveType ?? "-",
        PathNodeType.OtherComponent => SelectedNode.ComponentType ?? "-",
        _ => "-"
    };

    public string LeakageLimitText => SelectedNode?.LeakageLimit is null ? "-" : $"{SelectedNode.LeakageLimit:0.###} L/min";

    public string TestPressureText => SelectedNode?.TestPressure is null ? "-" : $"{SelectedNode.TestPressure:0.###} MPa";

    public Visibility PenetrationVisibility => SelectedNode?.NodeType == PathNodeType.Penetration ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ValveVisibility => SelectedNode?.NodeType == PathNodeType.Valve ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OtherComponentVisibility => SelectedNode?.NodeType == PathNodeType.OtherComponent ? Visibility.Visible : Visibility.Collapsed;

    public bool CanCreateSystem => true;

    public bool CanCreatePenetration => SelectedNode is null || SelectedNode.NodeType == PathNodeType.System;

    public bool CanCreateValve => SelectedNode?.NodeType is PathNodeType.System or PathNodeType.Penetration;

    public bool CanCreateOtherComponent => SelectedNode is null || SelectedNode.NodeType is PathNodeType.System or PathNodeType.Penetration;

    public string GetNextCode(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => $"SYS-{_systemSequence:000}",
            PathNodeType.Penetration => $"IPNI{_penetrationSequence:00}",
            PathNodeType.Valve => $"1RHR{_valveSequence:000}VP",
            PathNodeType.OtherComponent => $"SEAL-{_componentSequence:000}",
            _ => string.Empty
        };
    }

    public bool CanCreateNode(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => CanCreateSystem,
            PathNodeType.Penetration => CanCreatePenetration,
            PathNodeType.Valve => CanCreateValve,
            PathNodeType.OtherComponent => CanCreateOtherComponent,
            _ => false
        };
    }

    public void AddNode(TestObjectPathNode node)
    {
        if (!CanCreateNode(node.NodeType))
        {
            return;
        }

        if (node.NodeType == PathNodeType.System || SelectedNode is null)
        {
            PathTree.Add(node);
        }
        else
        {
            SelectedNode.Children.Add(node);
        }

        IncrementSequence(node.NodeType);
        SelectedNode = node;
    }

    public TestObjectPathNode? LocateFirstMatch()
    {
        var keyword = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            LocateMessage = "\u8bf7\u5148\u8f93\u5165\u8981\u5b9a\u4f4d\u7684\u7f16\u53f7\u6216\u540d\u79f0\u3002";
            return null;
        }

        var matchedNode = Flatten(PathTree).FirstOrDefault(node =>
            Contains(node.Code, keyword) ||
            Contains(node.Name, keyword) ||
            Contains(node.DisplayName, keyword));

        if (matchedNode is null)
        {
            LocateMessage = $"\u672a\u627e\u5230\u5339\u914d\u8def\u5f84\uff1a{keyword}";
            return null;
        }

        SelectedNode = matchedNode;
        LocateMessage = $"\u5df2\u5b9a\u4f4d\uff1a{matchedNode.DisplayName}";
        return matchedNode;
    }

    private void RefreshProjects()
    {
        Projects.Clear();
        foreach (var projectName in _store.GetProjectNames())
        {
            Projects.Add(projectName);
        }

        if (!Projects.Contains(SelectedProject))
        {
            _selectedProject = Projects.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(SelectedProject));
            OnPropertyChanged(nameof(CurrentScopeText));
        }

        RefreshUnits();
    }

    private static IEnumerable<TestObjectPathNode> Flatten(IEnumerable<TestObjectPathNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private static bool Contains(string source, string keyword)
    {
        return source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshUnits()
    {
        Units.Clear();
        foreach (var unitName in _store.GetUnitNames(SelectedProject))
        {
            Units.Add(unitName);
        }

        if (!Units.Contains(SelectedUnit))
        {
            _selectedUnit = Units.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(SelectedUnit));
            OnPropertyChanged(nameof(CurrentScopeText));
        }
    }

    private void LoadPathTree()
    {
        ResetSequences();
        PathTree.Clear();

        if (string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(SelectedUnit))
        {
            SelectedNode = null;
            return;
        }

        if (SelectedProject == "\u6d77\u5357\u9879\u76ee" && SelectedUnit == "\u6d77\u5357 3 \u53f7\u673a\u7ec4")
        {
            LoadHainanUnit3();
        }
        else if (SelectedProject == "\u6d77\u5357\u9879\u76ee" && SelectedUnit == "\u6d77\u5357 4 \u53f7\u673a\u7ec4")
        {
            LoadHainanUnit4();
        }
        else if (SelectedProject == "\u6f33\u5dde\u9879\u76ee")
        {
            LoadZhangzhouSample();
        }
        else
        {
            PathTree.Add(CreateSystemNode("SYS-001", $"{SelectedUnit} \u9ed8\u8ba4\u7cfb\u7edf", "\u65b0\u5efa\u9879\u76ee/\u673a\u7ec4\u7684\u9ed8\u8ba4\u8def\u5f84\uff0c\u53ef\u7ee7\u7eed\u7ef4\u62a4\u8d2f\u7a7f\u4ef6\u3001\u9600\u95e8\u548c\u5176\u4ed6\u90e8\u4ef6\u3002", 0, 0, "-", "-", "-", "-"));
        }

        SelectedNode = PathTree.FirstOrDefault();
    }

    private void ResetSequences()
    {
        _systemSequence = 2;
        _penetrationSequence = 3;
        _valveSequence = 42;
        _componentSequence = 2;
    }

    private void IncrementSequence(PathNodeType nodeType)
    {
        switch (nodeType)
        {
            case PathNodeType.System:
                _systemSequence++;
                break;
            case PathNodeType.Penetration:
                _penetrationSequence++;
                break;
            case PathNodeType.Valve:
                _valveSequence++;
                break;
            case PathNodeType.OtherComponent:
                _componentSequence++;
                break;
        }
    }

    private void LoadHainanUnit3()
    {
        var rhr = CreateSystemNode("RHR", "\u4f59\u70ed\u6392\u51fa\u7cfb\u7edf", "\u6d77\u5357 3 \u53f7\u673a\u7ec4\u793a\u4f8b\u7cfb\u7edf\u8def\u5f84\u3002", 42, 1, "2026-05-26 12:18", "0.012 L/min", "\u5408\u683c", "DEV-001");
        var ipni01 = CreatePenetrationNode("IPNI01", "\u8d2f\u7a7f\u4ef6\u8def\u5f84", 0.08m, "\u8d2f\u7a7f\u4ef6\u53ef\u7ee7\u7eed\u6302\u63a5\u9600\u95e8\u6216\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6\u3002", 18, 1, "2026-05-26 12:18", "0.012 L/min", "\u5408\u683c", "DEV-001");

        ipni01.Children.Add(CreateValveNode("1RHR040VP", "\u9694\u79bb\u9600", "\u7535\u52a8\u9600", 0.05m, 0.9m, "\u8d2f\u7a7f\u4ef6\u4e0b\u7684\u9600\u95e8\u8def\u5f84\u3002", 6, 0, "2026-05-26 12:18", "0.012 L/min", "\u5408\u683c", "DEV-001"));
        ipni01.Children.Add(CreateValveNode("1RHR041VP", "\u9694\u79bb\u9600", "\u6b62\u56de\u9600", 0.05m, 0.9m, "\u8d2f\u7a7f\u4ef6\u4e0b\u7684\u9600\u95e8\u8def\u5f84\u3002", 5, 0, "2026-05-26 11:42", "0.018 L/min", "\u5408\u683c", "DEV-002"));
        rhr.Children.Add(ipni01);
        rhr.Children.Add(CreateOtherComponentNode("RHR-SEAL-01", "\u5bc6\u5c01\u6027\u90e8\u4ef6", "\u5bc6\u5c01\u578b", 0.06m, 0.8m, "\u7cfb\u7edf\u4e0b\u76f4\u63a5\u5efa\u7acb\u7684\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6\u8def\u5f84\u3002", 3, 1, "2026-05-25 16:05", "0.083 L/min", "\u4e0d\u5408\u683c", "DEV-003"));

        PathTree.Add(rhr);
    }

    private void LoadHainanUnit4()
    {
        var saf = CreateSystemNode("SAF", "\u5b89\u5168\u58f3\u9694\u79bb\u7cfb\u7edf", "\u6d77\u5357 4 \u53f7\u673a\u7ec4\u5b89\u5168\u58f3\u9694\u79bb\u76f8\u5173\u8def\u5f84\u3002", 16, 0, "2026-05-20 09:30", "0.015 L/min", "\u5408\u683c", "DEV-002");
        var ipni11 = CreatePenetrationNode("IPNI11", "\u5b89\u5168\u58f3\u8d2f\u7a7f\u4ef6", 0.07m, "\u6d77\u5357 4 \u53f7\u673a\u7ec4\u8d2f\u7a7f\u4ef6\u8def\u5f84\u3002", 9, 0, "2026-05-20 09:30", "0.015 L/min", "\u5408\u683c", "DEV-002");
        ipni11.Children.Add(CreateValveNode("4SAF101VP", "\u9694\u79bb\u9600", "\u622a\u6b62\u9600", 0.05m, 0.88m, "\u5b89\u5168\u58f3\u8d2f\u7a7f\u4ef6\u4e0b\u7684\u9600\u95e8\u3002", 4, 0, "2026-05-20 09:30", "0.015 L/min", "\u5408\u683c", "DEV-002"));
        saf.Children.Add(ipni11);
        PathTree.Add(saf);
    }

    private void LoadZhangzhouSample()
    {
        var cvc = CreateSystemNode("CVC", "\u5316\u5b66\u5bb9\u79ef\u63a7\u5236\u7cfb\u7edf", "\u6f33\u5dde\u9879\u76ee\u793a\u4f8b\u7cfb\u7edf\u8def\u5f84\u3002", 8, 1, "2026-05-18 15:12", "0.031 L/min", "\u5408\u683c", "DEV-Z01");
        var ipni21 = CreatePenetrationNode("IPNI21", "CVC \u8d2f\u7a7f\u4ef6", 0.08m, "\u6f33\u5dde\u9879\u76ee\u8d2f\u7a7f\u4ef6\u8def\u5f84\u3002", 5, 1, "2026-05-18 15:12", "0.031 L/min", "\u5408\u683c", "DEV-Z01");
        ipni21.Children.Add(CreateValveNode("1CVC021VP", "\u9694\u79bb\u9600", "\u7535\u52a8\u9600", 0.05m, 0.86m, "CVC \u8d2f\u7a7f\u4ef6\u4e0b\u7684\u9600\u95e8\u3002", 3, 1, "2026-05-18 15:12", "0.031 L/min", "\u5408\u683c", "DEV-Z01"));
        cvc.Children.Add(ipni21);
        PathTree.Add(cvc);
    }

    private static TestObjectPathNode CreateSystemNode(string code, string name, string remark, int totalTests, int failedTests, string latestTestTime, string latestLeakageRate, string latestResult, string latestDevice)
    {
        return new TestObjectPathNode { Code = code, Name = name, NodeType = PathNodeType.System, Remark = remark, TotalTests = totalTests, FailedTests = failedTests, LatestTestTime = latestTestTime, LatestLeakageRate = latestLeakageRate, LatestResult = latestResult, LatestDevice = latestDevice };
    }

    private static TestObjectPathNode CreatePenetrationNode(string code, string name, decimal leakageLimit, string remark, int totalTests, int failedTests, string latestTestTime, string latestLeakageRate, string latestResult, string latestDevice)
    {
        return new TestObjectPathNode { Code = code, Name = name, NodeType = PathNodeType.Penetration, LeakageLimit = leakageLimit, Remark = remark, TotalTests = totalTests, FailedTests = failedTests, LatestTestTime = latestTestTime, LatestLeakageRate = latestLeakageRate, LatestResult = latestResult, LatestDevice = latestDevice };
    }

    private static TestObjectPathNode CreateValveNode(string code, string name, string valveType, decimal leakageLimit, decimal testPressure, string remark, int totalTests, int failedTests, string latestTestTime, string latestLeakageRate, string latestResult, string latestDevice)
    {
        return new TestObjectPathNode { Code = code, Name = name, NodeType = PathNodeType.Valve, ValveType = valveType, LeakageLimit = leakageLimit, TestPressure = testPressure, Remark = remark, TotalTests = totalTests, FailedTests = failedTests, LatestTestTime = latestTestTime, LatestLeakageRate = latestLeakageRate, LatestResult = latestResult, LatestDevice = latestDevice };
    }

    private static TestObjectPathNode CreateOtherComponentNode(string code, string name, string componentType, decimal leakageLimit, decimal testPressure, string remark, int totalTests, int failedTests, string latestTestTime, string latestLeakageRate, string latestResult, string latestDevice)
    {
        return new TestObjectPathNode { Code = code, Name = name, NodeType = PathNodeType.OtherComponent, ComponentType = componentType, LeakageLimit = leakageLimit, TestPressure = testPressure, Remark = remark, TotalTests = totalTests, FailedTests = failedTests, LatestTestTime = latestTestTime, LatestLeakageRate = latestLeakageRate, LatestResult = latestResult, LatestDevice = latestDevice };
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(NodeTypeText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(AvailableCreateText));
        OnPropertyChanged(nameof(NodeOperationDescription));
        OnPropertyChanged(nameof(LimitLabel));
        OnPropertyChanged(nameof(PressureLabel));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(TypeValue));
        OnPropertyChanged(nameof(LeakageLimitText));
        OnPropertyChanged(nameof(TestPressureText));
        OnPropertyChanged(nameof(PenetrationVisibility));
        OnPropertyChanged(nameof(ValveVisibility));
        OnPropertyChanged(nameof(OtherComponentVisibility));
        OnPropertyChanged(nameof(CanCreatePenetration));
        OnPropertyChanged(nameof(CanCreateValve));
        OnPropertyChanged(nameof(CanCreateOtherComponent));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
