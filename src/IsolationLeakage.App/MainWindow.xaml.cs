using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace IsolationLeakage.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private string _currentPageKey = "Overview";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PreviewRecord> PreviewRecords { get; } =
    [
        new("1RHR040VP", "\u6d77\u5357 3 \u53f7", "0.012 L/min", "\u5408\u683c", "2026-05-26 12:18"),
        new("1RHR041VP", "\u6d77\u5357 3 \u53f7", "0.018 L/min", "\u5408\u683c", "2026-05-26 11:42"),
        new("1RHR042VP", "\u6d77\u5357 3 \u53f7", "0.083 L/min", "\u4e0d\u5408\u683c", "2026-05-26 10:16"),
        new("1RCV010VP", "\u6d77\u5357 3 \u53f7", "0.009 L/min", "\u5408\u683c", "2026-05-25 16:05"),
    ];

    public string CurrentPageTitle => _currentPageKey switch
    {
        "Overview" => "\u9996\u9875\u6982\u89c8",
        "MasterData" => "\u57fa\u7840\u53f0\u8d26",
        "RealtimeMonitor" => "\u5b9e\u65f6\u76d1\u89c6",
        "Records" => "\u8bd5\u9a8c\u8bb0\u5f55",
        "Analysis" => "\u7edf\u8ba1\u5206\u6790",
        "SystemManagement" => "\u7cfb\u7edf\u7ba1\u7406",
        _ => "\u9996\u9875\u6982\u89c8"
    };

    public Visibility OverviewVisibility => _currentPageKey == "Overview" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MasterDataVisibility => _currentPageKey == "MasterData" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RealtimeMonitorVisibility => _currentPageKey == "RealtimeMonitor" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecordsVisibility => _currentPageKey == "Records" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnalysisVisibility => _currentPageKey == "Analysis" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SystemManagementVisibility => _currentPageKey == "SystemManagement" ? Visibility.Visible : Visibility.Collapsed;

    public bool IsOverviewActive => _currentPageKey == "Overview";
    public bool IsMasterDataActive => _currentPageKey == "MasterData";
    public bool IsRealtimeMonitorActive => _currentPageKey == "RealtimeMonitor";
    public bool IsRecordsActive => _currentPageKey == "Records";
    public bool IsAnalysisActive => _currentPageKey == "Analysis";
    public bool IsSystemManagementActive => _currentPageKey == "SystemManagement";

    private void Navigate(string pageKey)
    {
        if (_currentPageKey == pageKey)
        {
            return;
        }

        _currentPageKey = pageKey;
        OnPropertyChanged(string.Empty);
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => Navigate("Overview");
    private void MasterDataNav_Click(object sender, RoutedEventArgs e) => Navigate("MasterData");
    private void RealtimeMonitorNav_Click(object sender, RoutedEventArgs e) => Navigate("RealtimeMonitor");
    private void RecordsNav_Click(object sender, RoutedEventArgs e) => Navigate("Records");
    private void AnalysisNav_Click(object sender, RoutedEventArgs e) => Navigate("Analysis");
    private void SystemManagementNav_Click(object sender, RoutedEventArgs e) => Navigate("SystemManagement");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record PreviewRecord(string ObjectCode, string Unit, string LeakageRate, string Result, string UploadedAt);
