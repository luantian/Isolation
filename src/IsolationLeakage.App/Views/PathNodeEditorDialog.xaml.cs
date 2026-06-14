using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class PathNodeEditorDialog : Window, INotifyPropertyChanged
{
    private string _code;
    private string _errorMessage = string.Empty;
    private string _leakageLimitText;
    private string _nodeName;
    private string _remark;
    private string? _selectedType;
    private string _testPressureText;

    public PathNodeEditorDialog(PathNodeType nodeType, string defaultCode, TestObjectPathNode? parentNode)
    {
        NodeType = nodeType;
        _code = defaultCode;
        _nodeName = GetDefaultName(nodeType);
        _selectedType = GetDefaultType(nodeType);
        _leakageLimitText = GetDefaultLimit(nodeType);
        _testPressureText = GetDefaultPressure(nodeType);
        _remark = GetDefaultRemark(nodeType);
        TypeOptions = new ObservableCollection<string>(GetTypeOptions(nodeType));
        ParentHint = parentNode is null
            ? "\u521b\u5efa\u5728\u5f53\u524d\u673a\u7ec4\u6839\u8def\u5f84\u4e0b"
            : $"\u521b\u5efa\u5728\uff1a{parentNode.DisplayName}";

        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PathNodeType NodeType { get; }

    public TestObjectPathNode? ResultNode { get; private set; }

    public ObservableCollection<string> TypeOptions { get; }

    public string DialogTitle => NodeType switch
    {
        PathNodeType.System => "\u65b0\u5efa\u5de5\u827a\u7cfb\u7edf",
        PathNodeType.Penetration => "\u65b0\u5efa\u8d2f\u7a7f\u4ef6",
        PathNodeType.Valve => "\u65b0\u5efa\u9600\u95e8",
        PathNodeType.OtherComponent => "\u65b0\u5efa\u5176\u4ed6\u5bc6\u5c01\u6027\u90e8\u4ef6",
        _ => "\u65b0\u5efa\u8def\u5f84\u8282\u70b9"
    };

    public string ParentHint { get; }

    public string CodeLabel => NodeType switch
    {
        PathNodeType.System => "\u7cfb\u7edf\u7f16\u53f7",
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6\u7f16\u53f7",
        PathNodeType.Valve => "\u9600\u95e8\u7f16\u53f7",
        PathNodeType.OtherComponent => "\u90e8\u4ef6\u7f16\u53f7",
        _ => "\u8282\u70b9\u7f16\u53f7"
    };

    public string NameLabel => NodeType switch
    {
        PathNodeType.System => "\u7cfb\u7edf\u540d\u79f0",
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6\u540d\u79f0",
        PathNodeType.Valve => "\u9600\u95e8\u540d\u79f0",
        PathNodeType.OtherComponent => "\u90e8\u4ef6\u540d\u79f0",
        _ => "\u8282\u70b9\u540d\u79f0"
    };

    public string TypeLabel => NodeType == PathNodeType.Valve ? "\u9600\u95e8\u7c7b\u578b" : "\u90e8\u4ef6\u7c7b\u578b";

    public string LimitLabel => NodeType switch
    {
        PathNodeType.Penetration => "\u8d2f\u7a7f\u4ef6\u6cc4\u6f0f\u7387\u9650\u503c",
        PathNodeType.Valve => "\u9600\u95e8\u6cc4\u6f0f\u7387\u9650\u503c",
        PathNodeType.OtherComponent => "\u90e8\u4ef6\u6cc4\u6f0f\u7387\u9650\u503c",
        _ => "\u6cc4\u6f0f\u7387\u9650\u503c"
    };

    public string PressureLabel => NodeType == PathNodeType.Valve ? "\u9600\u95e8\u8bd5\u9a8c\u538b\u529b" : "\u8bd5\u9a8c\u538b\u529b";

    public Visibility TypeVisibility => NodeType is PathNodeType.Valve or PathNodeType.OtherComponent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LimitVisibility => NodeType is PathNodeType.Penetration or PathNodeType.Valve or PathNodeType.OtherComponent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PressureVisibility => NodeType is PathNodeType.Valve or PathNodeType.OtherComponent ? Visibility.Visible : Visibility.Collapsed;

    public string Code
    {
        get => _code;
        set => SetField(ref _code, value);
    }

    public string NodeName
    {
        get => _nodeName;
        set => SetField(ref _nodeName, value);
    }

    public string? SelectedType
    {
        get => _selectedType;
        set => SetField(ref _selectedType, value);
    }

    public string LeakageLimitText
    {
        get => _leakageLimitText;
        set => SetField(ref _leakageLimitText, value);
    }

    public string TestPressureText
    {
        get => _testPressureText;
        set => SetField(ref _testPressureText, value);
    }

    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            SetField(ref _errorMessage, value);
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateNode(out var node))
        {
            return;
        }

        ResultNode = node;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool TryCreateNode(out TestObjectPathNode node)
    {
        node = new TestObjectPathNode();

        if (string.IsNullOrWhiteSpace(Code))
        {
            ErrorMessage = $"{CodeLabel}\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(NodeName))
        {
            ErrorMessage = $"{NameLabel}\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        decimal? leakageLimit = null;
        decimal? testPressure = null;

        if (LimitVisibility == Visibility.Visible && !TryParseRequiredDecimal(LeakageLimitText, LimitLabel, out leakageLimit))
        {
            return false;
        }

        if (PressureVisibility == Visibility.Visible && !TryParseRequiredDecimal(TestPressureText, PressureLabel, out testPressure))
        {
            return false;
        }

        if (TypeVisibility == Visibility.Visible && string.IsNullOrWhiteSpace(SelectedType))
        {
            ErrorMessage = $"{TypeLabel}\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        node = new TestObjectPathNode
        {
            Code = Code.Trim(),
            Name = NodeName.Trim(),
            NodeType = NodeType,
            ValveType = NodeType == PathNodeType.Valve ? SelectedType : null,
            ComponentType = NodeType == PathNodeType.OtherComponent ? SelectedType : null,
            LeakageLimit = leakageLimit,
            TestPressure = testPressure,
            Remark = Remark.Trim()
        };

        return true;
    }

    private bool TryParseRequiredDecimal(string value, string label, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = $"{label}\u4e0d\u80fd\u4e3a\u7a7a\u3002";
            return false;
        }

        if (!decimal.TryParse(value.Trim(), out var parsed) || parsed < 0)
        {
            ErrorMessage = $"{label}\u5fc5\u987b\u4e3a\u975e\u8d1f\u6570\u5b57\u3002";
            return false;
        }

        result = parsed;
        return true;
    }

    private static string GetDefaultName(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => "\u65b0\u5efa\u5de5\u827a\u7cfb\u7edf",
            PathNodeType.Penetration => "\u65b0\u5efa\u8d2f\u7a7f\u4ef6",
            PathNodeType.Valve => "\u65b0\u5efa\u9600\u95e8",
            PathNodeType.OtherComponent => "\u65b0\u5efa\u5176\u4ed6\u90e8\u4ef6",
            _ => "\u65b0\u5efa\u8282\u70b9"
        };
    }

    private static string? GetDefaultType(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.Valve => "\u5f85\u786e\u8ba4\uff1a\u7535\u52a8\u9600",
            PathNodeType.OtherComponent => "\u5f85\u786e\u8ba4\uff1a\u5bc6\u5c01\u578b",
            _ => null
        };
    }

    private static string GetDefaultLimit(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.Penetration => "0.08",
            PathNodeType.Valve => "0.05",
            PathNodeType.OtherComponent => "0.06",
            _ => string.Empty
        };
    }

    private static string GetDefaultPressure(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.Valve => "0.9",
            PathNodeType.OtherComponent => "0.8",
            _ => string.Empty
        };
    }

    private static string GetDefaultRemark(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => "\u8bf7\u7ef4\u62a4\u7cfb\u7edf\u7f16\u53f7\u3001\u540d\u79f0\u548c\u5907\u6ce8\u3002",
            PathNodeType.Penetration => "\u8bf7\u7ef4\u62a4\u8d2f\u7a7f\u4ef6\u7f16\u53f7\u3001\u540d\u79f0\u548c\u6cc4\u6f0f\u7387\u9650\u503c\u3002",
            PathNodeType.Valve => "\u8bf7\u7ef4\u62a4\u9600\u95e8\u7c7b\u578b\u3001\u6cc4\u6f0f\u7387\u9650\u503c\u548c\u8bd5\u9a8c\u538b\u529b\u3002\u7c7b\u578b\u679a\u4e3e\u6807\u6ce8\u201c\u5f85\u786e\u8ba4\u201d\u65f6\uff0c\u8868\u793a\u8be5\u679a\u4e3e\u503c\u4e0d\u662f\u6587\u6863\u660e\u786e\u5199\u6b7b\u7684\u503c\u3002",
            PathNodeType.OtherComponent => "\u8bf7\u7ef4\u62a4\u90e8\u4ef6\u7c7b\u578b\u3001\u6cc4\u6f0f\u7387\u9650\u503c\u548c\u8bd5\u9a8c\u538b\u529b\u3002\u7c7b\u578b\u679a\u4e3e\u6807\u6ce8\u201c\u5f85\u786e\u8ba4\u201d\u65f6\uff0c\u8868\u793a\u8be5\u679a\u4e3e\u503c\u4e0d\u662f\u6587\u6863\u660e\u786e\u5199\u6b7b\u7684\u503c\u3002",
            _ => string.Empty
        };
    }

    private static IEnumerable<string> GetTypeOptions(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.Valve => ["\u5f85\u786e\u8ba4\uff1a\u7535\u52a8\u9600", "\u5f85\u786e\u8ba4\uff1a\u6b62\u56de\u9600", "\u5f85\u786e\u8ba4\uff1a\u95f8\u9600", "\u5f85\u786e\u8ba4\uff1a\u622a\u6b62\u9600", "\u5f85\u786e\u8ba4\uff1a\u7403\u9600"],
            PathNodeType.OtherComponent => ["\u5f85\u786e\u8ba4\uff1a\u5bc6\u5c01\u578b", "\u5f85\u786e\u8ba4\uff1a\u6cd5\u5170\u5bc6\u5c01", "\u5f85\u786e\u8ba4\uff1a\u7aef\u76d6\u5bc6\u5c01", "\u5f85\u786e\u8ba4\uff1a\u5176\u4ed6\u5bc6\u5c01"],
            _ => []
        };
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
